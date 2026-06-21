using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core;

namespace LauncherV2.Plugins.AnotherCrabsTreasure;

// ═══════════════════════════════════════════════════════════════════════════════
// AnotherCrabsTreasurePlugin — install / launch for "Another Crab's Treasure"
// played through the ACT-AP-Client-Plugin BepInEx mod
// (Automagic00/ACT-AP-Client-Plugin on GitHub).
//
// REALITY CHECK (2026-06-14) — facts verified online this session
// ─────────────────────────────────────────────────────────────────────────────
// Another Crab's Treasure is a Steam-NATIVE soulslike (Aggrocrab, 2024).
// The Archipelago integration is a BepInEx 5.x mod — exactly like Hollow Knight.
//
//   * STEAM APP ID: 1887840 (verified on store.steampowered.com/app/1887840/).
//     The README also references depot 1887840/1887841 for downpatching.
//     There is NO downpatching need for current users — version 0.5.0+ of the
//     mod targets the Year of the Crab update (the live Steam build). Version
//     0.4.4 is the last release for the pre-Year-of-the-Crab build.
//     This plugin always targets the LATEST release (currently 0.5.0), which
//     means it requires the current Steam build. Users who have downpatched
//     should use mod 0.4.4 manually — noted in the Settings panel.
//
//   * AP GAME STRING: "Another Crabs Treasure" (NO apostrophe in "Crabs") —
//     verified directly in AP Core Scripts/Plugin.cs:
//     session.TryConnectAndLogin("Another Crabs Treasure", ...) and
//     session.Locations.GetLocationIdFromName("Another Crabs Treasure", ...).
//
//   * MOD FRAMEWORK: BepInEx 5.x (net472).
//     Latest BepInEx stable: v5.4.23.5, asset: BepInEx_win_x64_5.4.23.5.zip.
//     CSPROJ confirms: BepInEx.Core 5.4.21, game Managed path =
//     <ACT>\AnotherCrabsTreasure_Data\Managed, plugins output =
//     <ACT>\BepInEx\plugins.
//
//   * MOD ASSET: ACTAP.dll (single file) — placed in <ACT>/BepInEx/plugins/.
//     Release 0.5.0 ships ACTAP.dll + another_crab.apworld.
//     Release 0.4.4 was the last pre-Year-of-the-Crab release (same assets).
//
//   * APWORLD FILENAME: "another_crab.apworld" (verified on release page).
//
//   * CONNECTION: entered IN-GAME via an IMGUI overlay on the title screen
//     (top-left form: server address, port, password, slot name; "Connect"
//     button). The mod stores nothing to a config file between sessions —
//     the fields are re-entered each time. There is NO command-line arg or
//     config file the launcher can pre-write. Therefore this plugin does NOT
//     attempt a connection prefill and states this honestly.
//     ConnectsItself = true: the mod owns the AP slot connection, so the
//     launcher must NOT hold its own ApClient on the same slot.
//
// HOW THIS PLUGIN WORKS
// ─────────────────────
//   1. Detect the Steam install via registry (HKCU SteamPath + libraryfolders
//      VDF scan for appmanifest_1887840.acf) with a manual override sidecar.
//   2. Install BepInEx 5 (latest Windows x64 zip from GitHub) into the game
//      folder if not already present (idempotent — checks for BepInEx/core/).
//   3. Download ACTAP.dll from the latest ACT-AP-Client-Plugin GitHub release
//      and drop it into <ACT>/BepInEx/plugins/. Fetches another_crab.apworld
//      next to the plugin install for the user to copy into Archipelago's
//      custom_worlds folder.
//   4. Launch = steam://rungameid/1887840 (or the direct EXE if found). The
//      user connects from the in-game title-screen overlay.
//   5. SupportsStandalone = true (vanilla game works fine with or without the
//      mod — BepInEx + ACTAP.dll have no effect until an AP connection is made).
//
// DEFENSIVE / UNVERIFIED ("verify at build time", per project convention)
// ─────────────────────────────────────────────────────────────────────────────
//   * BepInEx windows x64 asset filename pattern: "BepInEx_win_x64_*.zip" —
//     verified against current release v5.4.23.5 which ships exactly that name.
//   * Game EXE name: assumed "AnotherCrabsTreasure.exe" (Unity 2020.3 default
//     with the game's title sanitised). Falls back to any *.exe in the root.
//   * The mod's in-game connection fields do not persist between sessions (no
//     PlayerPrefs usage found in Plugin.cs / SaveSettingsToFile.cs). If a future
//     release adds config persistence, a prefill path can be added.
//   * Settings sidecar lives at Games/ROMs/another_crabs_treasure/
//     another_crabs_treasure_launcher.json (per brief spec). This plugin does NOT
//     modify Core/SettingsStore.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class AnotherCrabsTreasurePlugin : IGamePlugin
{
    // ── Constants — mod repo (Automagic00/ACT-AP-Client-Plugin) ─────────────
    private const string MOD_OWNER   = "Automagic00";
    private const string MOD_REPO    = "ACT-AP-Client-Plugin";
    private const string ModRepoUrl  = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";

    // ── Constants — BepInEx 5 (verified v5.4.23.5 asset pattern) ────────────
    private const string BEPINEX_OWNER = "BepInEx";
    private const string BEPINEX_REPO  = "BepInEx";
    private const string GH_BEPINEX_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{BEPINEX_OWNER}/{BEPINEX_REPO}/releases/latest";

    // Pinned fallback: mod v0.4.4 (last stable verified release) and BepInEx
    // v5.4.23.5 (latest stable at research time, 2026-06-14).
    private const string FallbackModVersion     = "0.4.4";
    private const string FallbackBepInExVersion = "5.4.23.5";
    private static readonly string FallbackModDllUrl =
        $"{ModRepoUrl}/releases/download/{FallbackModVersion}/ACTAP.dll";
    private static readonly string FallbackApWorldUrl =
        $"{ModRepoUrl}/releases/download/{FallbackModVersion}/another_crab.apworld";
    private static readonly string FallbackBepInExUrl =
        $"https://github.com/{BEPINEX_OWNER}/{BEPINEX_REPO}/releases/download/" +
        $"v{FallbackBepInExVersion}/BepInEx_win_x64_{FallbackBepInExVersion}.zip";

    // ── Constants — Steam ────────────────────────────────────────────────────
    private const string SteamAppId  = "1887840";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    // ── HTTP client (shared) ─────────────────────────────────────────────────
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "another_crabs_treasure";
    public string DisplayName => "Another Crab's Treasure";
    public string Subtitle    => "Steam soulslike · BepInEx AP mod";

    /// EXACT AP game string — verified in ACT-AP-Client-Plugin/AP Core Scripts/
    /// Plugin.cs: session.TryConnectAndLogin("Another Crabs Treasure", ...).
    /// NOTE: No apostrophe in "Crabs" — this is intentional.
    public string ApWorldName => "Another Crabs Treasure";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "another_crabs_treasure.png");

    public string ThemeAccentColor => "#2E6B8A";   // ocean teal

    public string[] GameBadges => new[] { "Steam · BepInEx mod" };

    public string Description =>
        "Another Crab's Treasure is an action soulslike by Aggrocrab set in a " +
        "polluted ocean. Play as Kril, a hermit crab on a quest to reclaim his " +
        "stolen shell. The Archipelago mod (ACT-AP-Client-Plugin, BepInEx) " +
        "randomizes adaptations, skills, currency pickups, stowaways, upgrade " +
        "pickups, and map pieces into the multiworld. The game connects to the " +
        "Archipelago server through an in-game overlay on the title screen. You " +
        "bring your own copy of Another Crab's Treasure (Steam), and the launcher " +
        "installs BepInEx and the AP mod on top. Start a new save after connecting.";

    public string? VideoPreviewUrl  => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means ACTAP.dll is present in the game's BepInEx/plugins folder.
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Flags ─────────────────────────────────────────────────────────────────

    /// The ACT-AP mod owns the AP slot connection (in-game overlay). The launcher
    /// must not hold its own ApClient on the same slot while the mod is connected.
    public bool ConnectsItself   => true;

    /// Vanilla Another Crab's Treasure runs fine; BepInEx + mod have no effect
    /// when no AP connection is made.
    public bool SupportsStandalone => true;

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Working dir for downloads / stamps. The actual mod is dropped INTO the
    /// game's BepInEx/plugins folder (not here). Exposed as GameDirectory per the
    /// interface contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "AnotherCrabsTreasure");

    /// Sidecar dir for plugin settings (as specified in the task brief).
    private string SidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", "another_crabs_treasure");

    private string SidecarPath
        => Path.Combine(SidecarDir, "another_crabs_treasure_launcher.json");

    private string VersionStampPath
        => Path.Combine(SidecarDir, "act_mod_version.dat");

    private string ApWorldSavePath
        => Path.Combine(GameDirectory, "another_crab.apworld");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The ACT-AP mod communicates with the AP server directly. These events exist
    // for interface compatibility only (ConnectsItself = true).
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
                ? (ReadVersionStamp() ?? "installed")
                : null;
        }
        catch { InstalledVersion = null; }
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
        // 0. Need the game install to put BepInEx + the mod into.
        progress.Report((2, "Locating your Another Crab's Treasure installation..."));
        string? gameDir = ResolveGameDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find Another Crab's Treasure in your Steam library. " +
                "Please install it via Steam, or open this game's Settings and " +
                "select the install folder manually.");

        string pluginsDir = Path.Combine(gameDir, "BepInEx", "plugins");
        string bepinexDir = Path.Combine(gameDir, "BepInEx");

        // 1. Install BepInEx 5 if not already present.
        progress.Report((6, "Checking BepInEx installation..."));
        if (!IsBepInExInstalled(gameDir))
        {
            progress.Report((8, "BepInEx not found — installing..."));
            await InstallBepInExAsync(gameDir, progress, ct);
        }
        else
        {
            progress.Report((20, "BepInEx already installed."));
        }

        // 2. Resolve the latest ACT-AP mod release.
        progress.Report((22, "Checking the latest ACT-AP mod release..."));
        var (version, dllUrl, apworldUrl) = await ResolveLatestModReleaseAsync(ct);
        AvailableVersion = version;

        if (dllUrl == null)
            throw new InvalidOperationException(
                "Could not find ACTAP.dll on the GitHub release page. Check your " +
                "internet connection, or download ACTAP.dll manually from " +
                ModRepoUrl + "/releases and place it in:\n" + pluginsDir);

        // 3. Download ACTAP.dll and drop it into BepInEx/plugins/.
        progress.Report((25, $"Downloading ACTAP.dll {version}..."));
        Directory.CreateDirectory(pluginsDir);
        byte[] dllBytes = await _http.GetByteArrayAsync(dllUrl, ct);
        string dllDest  = Path.Combine(pluginsDir, "ACTAP.dll");
        await File.WriteAllBytesAsync(dllDest, dllBytes, ct);
        progress.Report((75, "ACTAP.dll installed."));

        // 4. Fetch the apworld (best effort) and save next to our working dir.
        if (apworldUrl != null)
        {
            try
            {
                progress.Report((76, "Downloading another_crab.apworld..."));
                Directory.CreateDirectory(GameDirectory);
                byte[] apworld = await _http.GetByteArrayAsync(apworldUrl, ct);
                await File.WriteAllBytesAsync(ApWorldSavePath, apworld, ct);
                progress.Report((88, "another_crab.apworld saved — copy it into Archipelago's custom_worlds folder."));
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                progress.Report((88, "Could not download the apworld — get it from the GitHub release page."));
            }
        }

        // 5. Stamp the installed version.
        Directory.CreateDirectory(SidecarDir);
        await File.WriteAllTextAsync(VersionStampPath, version, ct);
        InstalledVersion = version;

        progress.Report((100,
            $"Another Crab's Treasure AP mod {version} installed. " +
            "To play: launch the game, fill in your server address, port, slot name, " +
            "and password in the in-game overlay (top-left of the title screen), " +
            "click Connect, then start a new save file."));
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
        // HONEST: The ACT-AP mod connection is entered via the IN-GAME title-screen
        // overlay (server/port/slot/password fields). The mod stores no config file
        // between sessions (verified in Plugin.cs / SaveSettingsToFile.cs). There
        // is no command-line arg or config file the launcher can pre-write.
        // The session credentials are intentionally not used for prefill — the user
        // enters them in-game. ConnectsItself = true.
        _ = session; // unused — no prefill mechanism exists for this mod
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
        // No plaintext AP password written to disk by this plugin.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;  // mod handles items via the AP server directly

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Honest intro ──────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "Another Crab's Treasure is your own Steam game with the ACT-AP " +
                "BepInEx mod added on top. The launcher detects your Steam install, " +
                "installs BepInEx 5, and drops in ACTAP.dll. After launching the game, " +
                "enter your Archipelago server address, port, slot name, and password " +
                "in the in-game overlay (top-left of the title screen), then click " +
                "Connect and start a new save. The in-game form must be filled each " +
                "session — the mod does not persist those settings between runs.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Version note ──────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "NOTE: Mod version 0.5.0 and later require the Year of the Crab " +
                "update (the current live Steam build). If you have downpatched your " +
                "game, install mod version 0.4.4 manually from the GitHub releases page.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: game install ─────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GAME INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? gameDir    = ResolveGameDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg  = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Steam install detected: " + gameDir)
            : "Another Crab's Treasure not detected. Select the install folder below, " +
              "or install the game via Steam first.";

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // mod DLL status
        string? modDll = FindInstalledModDll();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                ? "AP mod (ACTAP.dll) found: " + modDll
                : "ACTAP.dll not found in BepInEx/plugins yet (click Install on the Play tab).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        // install folder override picker
        var dirRow = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text        = overrideDir ?? gameDir ?? "",
            IsReadOnly  = true, FontSize = 12,
            Margin      = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "The Another Crab's Treasure Steam install folder. Auto-detected; " +
                          "select here to override (non-standard Steam library, etc.).",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content     = "Select folder...", Width = 120, Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select your Another Crab's Treasure install folder",
                InitialDirectory = Directory.Exists(overrideDir ?? gameDir ?? "")
                                   ? (overrideDir ?? gameDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                if (!LooksLikeActDir(picked))
                {
                    System.Windows.MessageBox.Show(
                        "That does not look like an Another Crab's Treasure installation. " +
                        "Select the folder that contains AnotherCrabsTreasure.exe or the " +
                        "AnotherCrabsTreasure_Data folder.",
                        "Not an ACT folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                SaveOverrideDir(picked);
                dirBox.Text = picked;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(dirBtn, Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "BepInEx and ACTAP.dll are installed into this folder by the " +
                   "Install button on the Play tab.",
            FontSize = 10, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 14),
        });

        // apworld note
        if (File.Exists(ApWorldSavePath))
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "another_crab.apworld is saved in " + GameDirectory +
                       @" — copy it into your Archipelago custom_worlds folder " +
                       @"(default: C:\ProgramData\Archipelago\custom_worlds) to host or " +
                       "generate multiworlds.",
                FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14),
            });
        }

        // ── Section: links ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 8, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("ACT-AP-Client-Plugin (GitHub) ↗", ModRepoUrl),
            ("Another Crab's Treasure on Steam ↗",
             $"https://store.steampowered.com/app/{SteamAppId}/"),
            ("Archipelago Official ↗", "https://archipelago.gg"),
            ("Archipelago Discord (ACT thread) ↗",
             "https://discord.com/channels/731205301247803413/1239467743116525688"),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content         = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding         = new Thickness(0, 2, 0, 2),
                Background      = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize        = 12, Margin = new Thickness(0, 0, 0, 4),
                Foreground      = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor          = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(u) { UseShellExecute = true });
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
            string json = await _http.GetStringAsync(GH_MOD_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : "",
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: el.TryGetProperty("tag_name", out var t) ? t.GetString()  ?? "" : "",
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var u) ? u.GetString() : null
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — game detection ─────────────────────────────────────

    /// Resolve the ACT install directory: override sidecar > Steam auto-detect.
    private string? ResolveGameDir()
    {
        string? over = LoadOverrideDir();
        if (over != null && LooksLikeActDir(over)) return over;

        return FindSteamActDir();
    }

    /// Look for appmanifest_1887840.acf across all Steam library folders. Returns
    /// the "steamapps/common/Another Crab's Treasure" path when found, else null.
    private static string? FindSteamActDir()
    {
        try
        {
            string? steamPath = FindSteamPath();
            if (steamPath == null) return null;

            foreach (string libRoot in EnumerateSteamLibraries(steamPath))
            {
                string manifest = Path.Combine(libRoot, "steamapps",
                    $"appmanifest_{SteamAppId}.acf");
                if (!File.Exists(manifest)) continue;

                // Read "installdir" from the ACF (simple key-value, not full VDF).
                string? installDir = ReadAcfInstallDir(manifest);
                if (installDir == null) continue;

                string gameDir = Path.Combine(libRoot, "steamapps", "common", installDir);
                if (LooksLikeActDir(gameDir)) return gameDir;

                // Fallback: try the canonical folder name.
                string canonical = Path.Combine(libRoot, "steamapps", "common",
                    "Another Crab's Treasure");
                if (LooksLikeActDir(canonical)) return canonical;
            }
        }
        catch { /* registry or filesystem failure — fall through */ }
        return null;
    }

    private static string? FindSteamPath()
    {
        // HKCU first, then HKLM WOW6432Node (same precedence as HollowKnight).
        using var hkcu = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Valve\Steam");
        if (hkcu?.GetValue("SteamPath") is string s1 && Directory.Exists(s1))
            return s1;

        using var hklm = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\WOW6432Node\Valve\Steam");
        if (hklm?.GetValue("InstallPath") is string s2 && Directory.Exists(s2))
            return s2;

        return null;
    }

    /// Parse libraryfolders.vdf to enumerate all Steam library roots. Tolerant
    /// (hand-written — no third-party VDF lib).
    private static IEnumerable<string> EnumerateSteamLibraries(string steamPath)
    {
        yield return steamPath;   // default library = Steam install dir

        string vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        string text;
        try { text = File.ReadAllText(vdf); }
        catch { yield break; }

        // Match: "path"  "<value>" (quoted, any whitespace between key and value).
        bool inNextLine = false;
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();

            if (inNextLine)
            {
                inNextLine = false;
                string? path = ExtractQuotedValue(line, "path");
                if (path != null && Directory.Exists(path)) yield return path;
                continue;
            }

            // Modern format: "path" on a dedicated key line.
            string? p = ExtractQuotedValue(line, "path");
            if (p != null && Directory.Exists(p)) { yield return p; continue; }

            // Legacy numeric-key block: the path line follows the block open.
            if (line.StartsWith("\"") && int.TryParse(line.Trim('"'), out _))
                inNextLine = true;
        }
    }

    private static string? ExtractQuotedValue(string line, string key)
    {
        int k = line.IndexOf($"\"{key}\"", StringComparison.OrdinalIgnoreCase);
        if (k < 0) return null;
        int after = k + key.Length + 2;
        int q1 = line.IndexOf('"', after);
        if (q1 < 0) return null;
        int q2 = line.IndexOf('"', q1 + 1);
        if (q2 < 0) return null;
        string val = line[(q1 + 1)..q2];
        return string.IsNullOrWhiteSpace(val) ? null : val.Replace("\\\\", "\\");
    }

    private static string? ReadAcfInstallDir(string acfPath)
    {
        try
        {
            foreach (string line in File.ReadLines(acfPath))
            {
                string? v = ExtractQuotedValue(line, "installdir");
                if (v != null) return v;
            }
        }
        catch { /* ignore */ }
        return null;
    }

    /// A directory "looks like" an ACT install when the EXE or the Data folder
    /// is present.
    private static bool LooksLikeActDir(string dir)
    {
        if (!Directory.Exists(dir)) return false;
        return File.Exists(Path.Combine(dir, "AnotherCrabsTreasure.exe"))
            || Directory.Exists(Path.Combine(dir, "AnotherCrabsTreasure_Data"));
    }

    // ── Private helpers — BepInEx ─────────────────────────────────────────────

    /// BepInEx 5 is considered installed when the core assemblies folder exists.
    private static bool IsBepInExInstalled(string gameDir)
        => Directory.Exists(Path.Combine(gameDir, "BepInEx", "core"));

    /// Download and extract BepInEx 5 (Windows x64) into the game folder.
    private async Task InstallBepInExAsync(
        string gameDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        var (bepVersion, bepZipUrl) = await ResolveLatestBepInExAsync(ct);

        progress.Report((10, $"Downloading BepInEx {bepVersion}..."));
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"bepinex-act-{bepVersion}-{Guid.NewGuid():N}.zip");
        try
        {
            using var resp = await _http.GetAsync(
                bepZipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            long total = resp.Content.Headers.ContentLength ?? -1;
            long downloaded = 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(tempZip);
            var buf = new byte[81920];
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                downloaded += n;
                if (total > 0)
                    progress.Report(((int)(10 + 8 * downloaded / total),
                        $"Downloading BepInEx... {downloaded / 1_000_000} MB"));
            }
            await dst.FlushAsync(ct);

            progress.Report((18, "Extracting BepInEx..."));
            System.IO.Compression.ZipFile.ExtractToDirectory(
                tempZip, gameDir, overwriteFiles: true);
            progress.Report((20, $"BepInEx {bepVersion} installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    /// Resolve the latest BepInEx 5 Windows x64 zip from GitHub releases.
    private async Task<(string Version, string ZipUrl)> ResolveLatestBepInExAsync(
        CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_BEPINEX_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            string  ver = tag != null && tag.StartsWith('v') ? tag[1..] : (tag ?? FallbackBepInExVersion);

            if (root.TryGetProperty("assets", out var assets) &&
                assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string low = name.ToLowerInvariant();
                    // e.g. BepInEx_win_x64_5.4.23.5.zip
                    if (low.Contains("win") && low.Contains("x64") && low.EndsWith(".zip"))
                        return (ver, url);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* fall through to pinned fallback */ }

        return (FallbackBepInExVersion, FallbackBepInExUrl);
    }

    // ── Private helpers — mod release ─────────────────────────────────────────

    /// Resolve the latest ACT-AP-Client-Plugin release: version, ACTAP.dll URL,
    /// apworld URL. Falls back to pinned 0.4.4 values when offline.
    private async Task<(string Version, string? DllUrl, string? ApWorldUrl)>
        ResolveLatestModReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_MOD_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? ver = root.TryGetProperty("tag_name", out var t) ? t.GetString()?.Trim() : null;
            if (string.IsNullOrEmpty(ver)) ver = FallbackModVersion;

            string? dllUrl = null, apworldUrl = null;
            if (root.TryGetProperty("assets", out var assets) &&
                assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    if (name.Equals("ACTAP.dll", StringComparison.OrdinalIgnoreCase))
                        dllUrl = url;
                    else if (name.EndsWith(".apworld", StringComparison.OrdinalIgnoreCase))
                        apworldUrl = url;
                }
            }

            return (ver, dllUrl, apworldUrl);
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        // Offline fallback — pinned 0.4.4 URLs verified live 2026-06-14.
        return (FallbackModVersion, FallbackModDllUrl, FallbackApWorldUrl);
    }

    // ── Private helpers — mod DLL detection ──────────────────────────────────

    /// Return the path to ACTAP.dll if it exists in the game's BepInEx/plugins/,
    /// else null.
    private string? FindInstalledModDll()
    {
        string? gameDir = ResolveGameDir();
        if (gameDir == null) return null;

        string dll = Path.Combine(gameDir, "BepInEx", "plugins", "ACTAP.dll");
        return File.Exists(dll) ? dll : null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartGame()
    {
        string? gameDir = ResolveGameDir();

        // Prefer direct EXE launch (preserves working dir, avoids Steam focus steal).
        string? exe = null;
        if (gameDir != null)
        {
            string preferred = Path.Combine(gameDir, "AnotherCrabsTreasure.exe");
            if (File.Exists(preferred))
                exe = preferred;
            else
            {
                // Fuzzy fallback: first *.exe in the install root.
                try
                {
                    foreach (string f in Directory.EnumerateFiles(
                        gameDir, "*.exe", SearchOption.TopDirectoryOnly))
                    {
                        exe = f;
                        break;
                    }
                }
                catch { /* fall through to Steam URL */ }
            }
        }

        Process? proc;
        if (exe != null)
        {
            proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = gameDir!,
                UseShellExecute  = false,
            });
        }
        else
        {
            // Fall back to Steam URL launch (always works when Steam is installed).
            proc = Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
        }

        if (proc == null)
            throw new InvalidOperationException(
                "Failed to launch Another Crab's Treasure. Make sure the game is " +
                "installed in Steam and try again.");

        _gameProcess = proc;
        IsRunning    = true;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            IsRunning    = false;
            _gameProcess = null;
            GameExited?.Invoke(proc.ExitCode);
        };
    }

    // ── Private helpers — sidecar (settings persistence) ─────────────────────

    private string? LoadOverrideDir()
    {
        try
        {
            if (!File.Exists(SidecarPath)) return null;
            string json = File.ReadAllText(SidecarPath);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("game_directory", out var v)
                   && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch { return null; }
    }

    private void SaveOverrideDir(string dir)
    {
        try
        {
            Directory.CreateDirectory(SidecarDir);
            var obj = new Dictionary<string, string> { ["game_directory"] = dir };
            File.WriteAllText(SidecarPath,
                JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }

    private string? ReadVersionStamp()
    {
        try
        {
            return File.Exists(VersionStampPath)
                ? File.ReadAllText(VersionStampPath).Trim()
                : null;
        }
        catch { return null; }
    }
}
