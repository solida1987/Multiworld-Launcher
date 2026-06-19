using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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

namespace LauncherV2.Plugins.TurnipBoy;

// ═══════════════════════════════════════════════════════════════════════════════
// TurnipBoyPlugin — install / launch for "Turnip Boy Commits Tax Evasion"
// (Snoozy Kazoo, 2021 — Steam appid 1205450) played through the
// TurnipBoyRandomizer BepInEx client mod by pointfivetee. This is a NATIVE
// "ConnectsItself" integration: the in-game mod connects to the AP server
// itself via text fields on a startup screen — no emulator, no Lua bridge, and
// no launcher-held ApClient on the slot.
//
// ── HONEST REALITY CHECK (2026-06-15, verified against repo + apworld) ────────
//
//   * THE AP WORLD game string is "TurnipBoy" — verified against
//     turnipboy/__init__.py: `game = "TurnipBoy"`. Repo:
//     github.com/pointfivetee/TurnipBoyRandomizer.
//
//   * THE MOD LOADER is BepInEx. The release zip "turnip_boy_mod.zip" ships
//     BepInEx pre-configured with the TBCTE_AP plugin. Installation = extract
//     the zip over the game folder (the conventional BepInEx layout drops
//     BepInEx/ and doorstop_config.ini next to the game exe). This IS
//     automatable — the launcher downloads and extracts it.
//
//   * THE CONNECTION is entered IN-GAME. After starting a game, text fields
//     appear for: server URI, player/slot name, and password. The mod stores
//     these in the game's own save data (keys ap_uri / ap_slot_name /
//     ap_password via the game's ReadWriteSaveManager), so there is NO config
//     file this launcher can pre-write. The plugin does NOT attempt a prefill
//     (verified against Plugin.cs + ArchipelagoClient.cs — no external file
//     path, all storage inside the save system).
//
//   * GITHUB RELEASE ASSETS (v0.1.3 verified 2026-06-15):
//       turnipboy.apworld        — the AP world (for the server-side generate step)
//       TurnipBoy.yaml           — example YAML for multiworld generation
//       turnip_boy_mod.zip       — the BepInEx client mod (what the launcher installs)
//
//   * THE GAME EXE is "Turnip Boy Commits Tax Evasion.exe" inside a standard
//     Steam install (appid 1205450 → steamapps\common\Turnip Boy Commits Tax
//     Evasion\). Verified against the Steam game page.
//
//   * "INSTALLED" is judged by the presence of BepInEx\plugins\TBCTE_AP.dll
//     (or any TBCTE*.dll) in the detected game folder — placed there by
//     extracting turnip_boy_mod.zip.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam install via the Windows registry + libraryfolders.vdf
//      (appmanifest_1205450.acf → steamapps\common\Turnip Boy Commits Tax
//      Evasion). A manual folder override is also supported and persisted in
//      this plugin's OWN sidecar (Games/ROMs/turnip_boy/tb_launcher.json).
//   2. INSTALL/UPDATE = download turnip_boy_mod.zip from the latest GitHub
//      release and extract it over the game folder (BepInEx layout). A version
//      stamp is written into the sidecar.
//   3. LAUNCH = run "Turnip Boy Commits Tax Evasion.exe" from the install dir;
//      fall back to steam://rungameid/1205450. No connection prefill.
//      ConnectsItself = true (the mod owns the slot).
//      SupportsStandalone = true (the unmodded game runs fine).
//   4. SETTINGS PANEL = install-dir picker, status lines (BepInEx + plugin
//      present), guided setup steps, and links.
//   5. NEWS = parses GitHub releases of the mod repo.
//
// ── DEFENSIVE NOTES ───────────────────────────────────────────────────────────
//   * Steam VDF / ACF parsing is tolerant; any failure degrades to null and
//     the manual picker is the fallback.
//   * The zip extraction is overwrite-safe; no partial-state is left on cancel.
//   * No AP password is ever written to disk by this plugin.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class TurnipBoyPlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const string MOD_OWNER   = "pointfivetee";
    private const string MOD_REPO    = "TurnipBoyRandomizer";
    private const string ModRepoUrl  = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";

    private const string GH_RELEASES_LATEST =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_RELEASES =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    // Pinned fallback when the GitHub API is unreachable.
    // v0.1.3 verified live 2026-06-15; asset "turnip_boy_mod.zip".
    private const string FallbackVersion   = "0.1.3";
    private const string FallbackModZipUrl =
        $"{ModRepoUrl}/releases/download/v{FallbackVersion}/turnip_boy_mod.zip";

    private const string SteamAppId            = "1205450";
    private const string SteamCommonFolderName = "Turnip Boy Commits Tax Evasion";
    private const string GameExeName           = "Turnip Boy Commits Tax Evasion.exe";

    private const string SetupGuideUrl  =
        "https://archipelago.gg/tutorial/TurnipBoy/setup/en";
    private const string ArchipelagoSite = "https://archipelago.gg";
    private const string BepInExSite    = "https://github.com/BepInEx/BepInEx";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "turnip_boy";
    public string DisplayName => "Turnip Boy Commits Tax Evasion";
    public string Subtitle    => "Native PC · BepInEx AP mod";

    /// EXACT AP game string — verified against turnipboy/__init__.py
    /// (`game = "TurnipBoy"`). Repo: pointfivetee/TurnipBoyRandomizer.
    public string ApWorldName => "TurnipBoy";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "turnip_boy.png");

    public string ThemeAccentColor => "#3A7A2A";   // vegetable-patch green
    public string[] GameBadges     => new[] { "Steam · needs BepInEx mod" };

    public string Description =>
        "Turnip Boy Commits Tax Evasion, the comedic action-adventure by Snoozy " +
        "Kazoo, played through the TurnipBoyRandomizer Archipelago mod by " +
        "pointfivetee. Inventory items and hats are shuffled into the multiworld; " +
        "the goal is to defeat Corrupt Onion. The mod is a BepInEx plugin that " +
        "connects to the Archipelago server from in-game text fields — no emulator, " +
        "no Lua bridge. You bring your own copy of the game (Steam appid 1205450); " +
        "the launcher detects your Steam install and installs the mod over it. " +
        "Server, slot name and password are entered in-game after starting a file.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" = the BepInEx AP plugin DLL is present under the game install.
    public bool IsInstalled => FindInstalledPluginDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory
    {
        get => ResolveInstallDir() ?? "";
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
                SaveOverrideDir(value);
        }
    }

    private string RomLibraryDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);

    private string SettingsSidecarPath
        => Path.Combine(RomLibraryDir, "tb_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The BepInEx mod holds the AP slot connection — the launcher relays nothing.
    // These exist for interface compatibility (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── ConnectsItself / Standalone ───────────────────────────────────────────

    /// The TurnipBoyRandomizer BepInEx mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    /// The unmodded game (or BepInEx without an AP connection) runs fine.
    public bool SupportsStandalone => true;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            InstalledVersion = FindInstalledPluginDll() != null
                ? (ReadStampedVersion() ?? "installed")
                : null;
        }
        catch { InstalledVersion = null; }

        try
        {
            var (version, _) = await ResolveLatestReleaseAsync(ct);
            AvailableVersion = version;
        }
        catch { AvailableVersion = null; } // contract: never throw on network failure
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 0. Need a game install to drop the mod into.
        progress.Report((2, "Locating your Turnip Boy Commits Tax Evasion installation..."));
        string? installDir = ResolveInstallDir();
        if (installDir == null)
            throw new InvalidOperationException(
                "Could not find a Turnip Boy Commits Tax Evasion installation. " +
                "Open this game's Settings and pick your install folder (the one " +
                "containing \"" + GameExeName + "\"), or install the game via Steam " +
                "first. The Archipelago mod is added on top of your own copy.");

        // 1. Resolve the latest release.
        progress.Report((6, "Checking the latest TurnipBoyRandomizer release..."));
        var (version, zipUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the TurnipBoyRandomizer mod download on GitHub. " +
                "Check your internet connection, or download it manually from " +
                ModRepoUrl + "/releases.");

        // 2. Download + extract the BepInEx mod zip over the game install dir.
        //    The zip uses the standard BepInEx layout — extracting at the game root
        //    drops BepInEx\ and doorstop_config.ini alongside the game exe.
        await DownloadAndExtractModAsync(zipUrl, version, installDir, progress, ct);

        // 3. Stamp the version.
        WriteStampedVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            $"TurnipBoyRandomizer mod {version} installed into your game folder. " +
            "To play: launch the game, start or load a file, enter your server " +
            "address, slot name, and password in the in-game text fields, then " +
            "click Connect. This launcher cannot pre-fill those fields — they are " +
            "entered in-game."));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // ── Lifecycle — ValidateExistingInstall ───────────────────────────────────

    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Turnip Boy install folder.";

        if (LooksLikeTurnipBoyDir(folder))
            return null;

        return "That does not look like a Turnip Boy Commits Tax Evasion installation " +
               "(\"" + GameExeName + "\" not found). Pick the folder that contains " +
               "the game exe.";
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // HONEST: connection is entered in-game (server / slot / password fields
        // in the in-game UI). There is no config file or CLI arg we can pre-fill
        // (verified — see header). Just launch the game; the user enters the
        // session credentials in-game.
        // ConnectsItself = true: do not hold an ApClient on this slot.
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
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself) ───────────────────────────────────

    public Task ReceiveItemsAsync(
        ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask; // mod receives items directly from the AP server

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

        string? installDir = ResolveInstallDir();
        string? pluginDll  = FindInstalledPluginDll();
        bool    bepInExOk  = installDir != null &&
                             Directory.Exists(Path.Combine(installDir, "BepInEx"));

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "Turnip Boy Commits Tax Evasion is your own game (Steam) with " +
                   "the TurnipBoyRandomizer BepInEx mod added on top. The launcher " +
                   "detects your Steam install and installs the mod by extracting " +
                   "turnip_boy_mod.zip over your game folder. You connect to your " +
                   "Archipelago server from in-game text fields after starting a " +
                   "file — there is no connection file this launcher can pre-fill. " +
                   "These in-game steps are not verified by this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: Install folder ───────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "GAME INSTALL FOLDER", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text        = installDir ?? "",
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "The folder that contains \"" + GameExeName + "\". " +
                          "Auto-detected from Steam; override here for non-default installs.",
        };
        var dirBtn = new Button
        {
            Content     = "Locate install...", Width = 130,
            Padding     = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Turnip Boy Commits Tax Evasion install folder",
                InitialDirectory = Directory.Exists(installDir) ? installDir!
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string? bad = ValidateExistingInstall(dlg.FolderName);
                if (bad != null)
                {
                    MessageBox.Show(bad, "Not a Turnip Boy folder",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                SaveOverrideDir(dlg.FolderName);
                dirBox.Text = dlg.FolderName;
            }
        };
        DockPanel.SetDock(dirBtn, Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new TextBlock
        {
            Text = installDir != null
                    ? "✓ Found (auto-detected from Steam appid " + SteamAppId + " or your override)."
                    : "Game not found — use \"Locate install...\" or install via Steam (appid " +
                      SteamAppId + ").",
            FontSize = 11, Foreground = installDir != null ? success : warn,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Mod status ───────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "MOD STATUS", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = bepInExOk
                    ? "✓ BepInEx folder found in your game install."
                    : "BepInEx not found — click Install on the Play tab (it will be extracted).",
            FontSize = 11, Foreground = bepInExOk ? success : warn,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(new TextBlock
        {
            Text = pluginDll != null
                    ? "✓ TurnipBoyRandomizer plugin found" +
                      (InstalledVersion != null && InstalledVersion != "installed"
                          ? " (v" + InstalledVersion + ")." : ".")
                    : "TurnipBoyRandomizer plugin not found — click Install on the Play tab.",
            FontSize = 11, Foreground = pluginDll != null ? success : warn,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Connecting ───────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "CONNECTING (entered in-game after starting a file)", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "After starting or loading a save file, text fields appear for " +
                   "your server address (e.g. archipelago.gg:38281), slot name, and " +
                   "optional password. Click Connect — the mod saves these credentials " +
                   "in the game's save data and reconnects automatically on future loads.",
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
            "1. Own Turnip Boy Commits Tax Evasion on Steam (appid 1205450). " +
                "If it was not detected above, use \"Locate install...\".",
            "2. Click Install on the Play tab. The launcher downloads the " +
                "TurnipBoyRandomizer mod zip and extracts BepInEx and the AP plugin " +
                "into your game folder.",
            "3. Launch the game from this launcher. BepInEx loads automatically " +
                "alongside the game.",
            "4. Start or load a save file. Text fields appear for your Archipelago " +
                "server address, slot name, and optional password. Enter them and " +
                "click Connect.",
            "5. Your server must have been generated with the TurnipBoy apworld " +
                "(turnipboy.apworld from the mod's GitHub releases). The launcher " +
                "cannot generate the server — use the Archipelago website or a local " +
                "AP install for that.",
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
            ("TurnipBoyRandomizer (GitHub) ↗",  ModRepoUrl),
            ("TurnipBoy Setup Guide ↗",         SetupGuideUrl),
            ("BepInEx (GitHub) ↗",              BepInExSite),
            ("Archipelago Official ↗",           ArchipelagoSite),
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
                Foreground          = new SolidColorBrush(Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor              = System.Windows.Input.Cursors.Hand,
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
            string json = await _http.GetStringAsync(GH_RELEASES, ct);
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
                    Version: el.TryGetProperty("tag_name", out var t)
                                 ? NormalizeTag(t.GetString()) ?? "" : "",
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var u) ? u.GetString() : null
                ));
                if (items.Count >= 20) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    /// Resolve the latest release: (version, turnip_boy_mod.zip URL).
    /// Returns the pinned fallback when the GitHub API is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestReleaseAsync(
        CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_LATEST, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null &&
                root.TryGetProperty("assets", out var assets) &&
                assets.ValueKind == JsonValueKind.Array)
            {
                string? modZip = null;
                string? anyZip = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u)
                                   ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;
                    anyZip ??= url;
                    // Prefer the client mod zip specifically.
                    if (modZip == null && lower.Contains("turnip_boy_mod"))
                        modZip = url;
                }
                string? zip = modZip ?? anyZip;
                if (zip != null)
                    return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (FallbackVersion, FallbackModZipUrl);
    }

    /// "v0.1.3" → "0.1.3"; otherwise trims.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    // ── Private helpers — Steam detection ─────────────────────────────────────

    private string? ResolveInstallDir()
    {
        string? ov = LoadOverrideDir();
        if (!string.IsNullOrWhiteSpace(ov) && ValidateExistingInstall(ov!) == null)
            return ov;

        try { return DetectSteamInstallDir(); }
        catch { return null; }
    }

    private static string? DetectSteamInstallDir()
    {
        foreach (string steamRoot in EnumerateSteamRoots())
        {
            foreach (string lib in EnumerateSteamLibraries(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string acf       = Path.Combine(steamapps, $"appmanifest_{SteamAppId}.acf");
                    if (!File.Exists(acf)) continue;

                    string? installDirName = ReadVdfValue(acf, "installdir");
                    string  common         = Path.Combine(steamapps, "common");
                    if (!string.IsNullOrWhiteSpace(installDirName))
                    {
                        string candidate = Path.Combine(common, installDirName!);
                        if (LooksLikeTurnipBoyDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeTurnipBoyDir(conventional)) return conventional;
                }
                catch { /* try next library */ }
            }
        }
        return null;
    }

    private static bool LooksLikeTurnipBoyDir(string dir)
    {
        try
        {
            return Directory.Exists(dir) &&
                   File.Exists(Path.Combine(dir, GameExeName));
        }
        catch { return false; }
    }

    private static IEnumerable<string> EnumerateSteamRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? root in new[]
        {
            ReadRegistryString(Registry.CurrentUser,
                               @"Software\Valve\Steam", "SteamPath"),
            ReadRegistryString(Registry.LocalMachine,
                               @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            ReadRegistryString(Registry.LocalMachine,
                               @"SOFTWARE\Valve\Steam", "InstallPath"),
        })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string norm = root!.Replace('/', '\\');
            if (Directory.Exists(norm) && seen.Add(norm))
                yield return norm;
        }
    }

    private static IEnumerable<string> EnumerateSteamLibraries(string steamRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (seen.Add(steamRoot)) yield return steamRoot;

        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        string[] lines;
        try { lines = File.ReadAllLines(vdf); }
        catch { yield break; }

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            string? val = TryReadQuotedKeyValue(line, "path");
            if (string.IsNullOrWhiteSpace(val)) continue;
            string norm = val!.Replace(@"\\", @"\").Replace('/', '\\');
            if (Directory.Exists(norm) && seen.Add(norm))
                yield return norm;
        }
    }

    private static string? ReadVdfValue(string path, string key)
    {
        try
        {
            foreach (string raw in File.ReadAllLines(path))
            {
                string? val = TryReadQuotedKeyValue(raw.Trim(), key);
                if (val != null) return val;
            }
        }
        catch { /* unreadable */ }
        return null;
    }

    private static string? TryReadQuotedKeyValue(string line, string key)
    {
        int k0 = line.IndexOf('"');
        if (k0 < 0) return null;
        int k1 = line.IndexOf('"', k0 + 1);
        if (k1 < 0) return null;
        string foundKey = line.Substring(k0 + 1, k1 - k0 - 1);
        if (!string.Equals(foundKey, key, StringComparison.OrdinalIgnoreCase))
            return null;
        int v0 = line.IndexOf('"', k1 + 1);
        if (v0 < 0) return null;
        int v1 = line.IndexOf('"', v0 + 1);
        if (v1 < 0) return null;
        return line.Substring(v0 + 1, v1 - v0 - 1);
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

    // ── Private helpers — installed-plugin detection ──────────────────────────

    /// Look for the TBCTE_AP BepInEx plugin DLL under the install's BepInEx tree.
    /// Returns the dll path, or null when not installed.
    private string? FindInstalledPluginDll()
    {
        try
        {
            string? install = ResolveInstallDir();
            if (install == null) return null;
            string bepInExDir = Path.Combine(install, "BepInEx");
            if (!Directory.Exists(bepInExDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(
                bepInExDir, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(dll);
                if (name.StartsWith("TBCTE", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("TBCTE_AP.dll", StringComparison.OrdinalIgnoreCase))
                    return dll;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartGame()
    {
        string? installDir = ResolveInstallDir();
        string? exe = installDir != null
            ? Path.Combine(installDir, GameExeName)
            : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = installDir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException(
                    "Failed to start Turnip Boy Commits Tax Evasion.");

            TrackProcess(proc);
            return;
        }

        // Fallback: Steam protocol
        try
        {
            var proc = Process.Start(new ProcessStartInfo(
                $"steam://rungameid/{SteamAppId}") { UseShellExecute = true });
            if (proc != null) TrackProcess(proc);
            IsRunning = true; // steam:// may not return a trackable handle
        }
        catch (Exception ex)
        {
            throw new FileNotFoundException(
                "Could not find \"" + GameExeName + "\". Open this game's " +
                "Settings and pick your install folder, or install the game via " +
                "Steam (appid " + SteamAppId + ").\n\n" + ex.Message,
                GameExeName);
        }
    }

    private void TrackProcess(Process proc)
    {
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
        catch { /* some shell-launched processes don't expose Exited — non-fatal */ }
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download turnip_boy_mod.zip and extract it over the game install dir.
    /// The BepInEx layout drops BepInEx\ and doorstop files next to the game exe.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string installDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"turnipboy-mod-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((10, $"Downloading TurnipBoyRandomizer mod {version}..."));
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
                        int pct = (int)(10 + 65 * downloaded / total);
                        progress.Report((pct,
                            $"Downloading mod... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((80, "Extracting BepInEx mod into game folder..."));
            // Extract at the game install root — BepInEx zips use paths like
            // BepInEx\plugins\TBCTE_AP.dll, doorstop_config.ini, etc.
            ZipFile.ExtractToDirectory(tempZip, installDir, overwriteFiles: true);
            progress.Report((95, "Mod files extracted."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    // ── Private helpers — settings sidecar ───────────────────────────────────

    private sealed class TbSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private TbSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<TbSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(TbSettings s)
    {
        try
        {
            Directory.CreateDirectory(RomLibraryDir);
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
