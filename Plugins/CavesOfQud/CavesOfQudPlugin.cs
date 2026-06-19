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

// NOTE: WPF UI types are FULLY QUALIFIED throughout this file (System.Windows.*).
// The real launcher project sets <UseWindowsForms>true</UseWindowsForms> alongside
// WPF, so the bare names Button / TextBox / Color / Brushes / MessageBox /
// FontWeights / Orientation / Clipboard collide between System.Windows.Forms and
// System.Windows[.Controls/.Media] (CS0104). Qualifying every UI type avoids that.
// GlobalUsings.cs already aliases the colliding short names project-wide — so this
// file must NOT add any file-level `using X = System.Windows...;` alias (CS1537).

namespace LauncherV2.Plugins.CavesOfQud;

// ═══════════════════════════════════════════════════════════════════════════════
// CavesOfQudPlugin — install / launch for "Caves of Qud" (Freehold Games,
// 2015/2024 full release) played through the CavesOfQudArchipelagoRandomizer
// mod by lonesurv1vor, which contains an in-game Archipelago client built on top
// of Qud's C# mod system. This is a NATIVE "ConnectsItself" integration in the
// same family as the shipped Blasphemous / Hollow Knight / TUNIC / Stardew Valley
// plugins: the game mod itself speaks to the AP server — no emulator, no Lua
// bridge, no launcher-held ApClient on the slot.
//
// Caves of Qud runs a C# mod system; mods are placed in a Mods/ folder inside the
// game installation. The mod is distributed as a release zip from GitHub, which
// is extracted to <CoQ>/Mods/. This plugin follows the same pattern as
// AShortHikePlugin.cs (Modding-folder mod drop) adapted for Qud's Mods/ layout.
//
// ── HONEST REALITY CHECK (community integration, unverified offline) ──────────
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of Caves of Qud (Steam appid 333640), and Archipelago support is delivered as a
// C# mod. The mod repo is lonesurv1vor/CavesOfQudArchipelagoRandomizer (community
// mod). The AP world name "Caves of Qud" is UNVERIFIED offline — verify against
// worlds/__init__.py when the apworld is available.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Caves of Qud install via the Windows registry, parsing
//      steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\Caves of Qud via appmanifest_333640.acf. A manual
//      install-dir OVERRIDE (settings folder picker) is also supported and takes
//      precedence; validated (must contain CoQ.exe) and persisted in this
//      plugin's OWN sidecar (Games/ROMs/caves_of_qud/caves_of_qud_launcher.json).
//   2. INSTALL/UPDATE = download the mod zip from
//      lonesurv1vor/CavesOfQudArchipelagoRandomizer/releases/latest, find any
//      .zip asset, and extract it into <CoQ>/Mods/. The mod likely lands under a
//      named subfolder (e.g. Mods/CavesOfQudAP/) — the zip structure is preserved
//      so the mod's own folder name is respected.
//   3. LAUNCH = run CoQ.exe from the detected/override install; if the exe cannot
//      be found but Steam is present, fall back to steam://rungameid/333640.
//      ConnectsItself = true (the mod owns the slot). SupportsStandalone = true
//      (plain Caves of Qud runs fine without AP). Connection is entered in-game.
//
// ── DEFENSIVE / UNVERIFIED ────────────────────────────────────────────────────
//   * "Installed" is judged by the presence of ANY .dll whose filename contains
//     "archipelago" (case-insensitive) anywhere under <CoQ>/Mods/, or by the
//     presence of the Mods/ directory itself containing a non-empty subfolder
//     that looks like an AP mod (contains "archipelago" in name or any dll).
//   * Steam library parsing is the same tolerant VDF scan used across all native
//     plugins; failures degrade to "not found" rather than throwing.
//   * Pinned fallback version: 0.1.0.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class CavesOfQudPlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────
    private const string MOD_OWNER = "lonesurv1vor";
    private const string MOD_REPO  = "CavesOfQudArchipelagoRandomizer";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string SetupGuideUrl   = "https://archipelago.gg/games/Caves%20of%20Qud/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Caves of Qud appid 333640.
    private const string CoqAppId = "333640";
    private static readonly string SteamRunUrl = $"steam://rungameid/{CoqAppId}";

    /// The standard Steam install sub-folder name for Caves of Qud.
    private const string SteamCommonFolderName = "Caves of Qud";

    /// The base-game executable name.
    private const string CoqExeName = "CoQ.exe";

    /// Pinned fallback version for the mod when the GitHub API is unreachable.
    private const string FallbackVersion = "0.1.0";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "caves_of_qud";
    public string DisplayName => "Caves of Qud";
    public string Subtitle    => "PC · Archipelago mod";

    /// AP world name — UNVERIFIED offline. Verify against worlds/__init__.py
    /// when the apworld becomes available.
    public string ApWorldName => "Caves of Qud";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "caves_of_qud.png");

    public string ThemeAccentColor => "#4CAF50";   // mutant green — Qud's iconic color
    public string[] GameBadges     => new[] { "Requires Caves of Qud on Steam" };

    public string Description =>
        "Caves of Qud (2015, full release 2024) is a deep roguelite RPG by Freehold Games " +
        "set in a far-future world of crumbling ruins, psychic mutants, and ancient chrome " +
        "relics. You emerge from the vine-hung jungles of Qud, making a living as a " +
        "scavenger, cultist, or wandering storyteller in a world with thousands of possible " +
        "histories. The Archipelago randomizer by lonesurv1vor integrates with Qud's C# mod " +
        "system: ancient artifacts, relic weapons, key story items, and exploration " +
        "milestones become multiworld location checks — and items from other games may " +
        "arrive as new mutations, gear, or legendary weapons. The launcher downloads the mod " +
        "and extracts it to your Mods folder. Requires: your own legally-owned copy of " +
        "Caves of Qud on Steam.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the Mods/ directory exists and contains a .dll with
    /// "archipelago" in its name (case-insensitive), anywhere under Mods/.
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Working directory for downloads. Actual mod is extracted INTO <CoQ>/Mods/.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "CavesOfQud");

    /// This plugin's OWN settings sidecar (install-dir override + version stamp).
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "caves_of_qud_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The mod reports checks/items/goal to the AP server itself — the launcher
    // relays nothing. These exist for interface compatibility (ConnectsItself = true).
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
        // 0. We need a Caves of Qud install to drop the mod into.
        progress.Report((2, "Locating your Caves of Qud installation..."));
        string? coqDir = ResolveCoqDir();
        if (coqDir == null)
            throw new InvalidOperationException(
                "Could not find a Caves of Qud installation. Open this game's Settings " +
                "and pick your Caves of Qud folder (the one containing CoQ.exe), " +
                "or install Caves of Qud via Steam first. The Archipelago mod is added " +
                "on top of your own copy of the game.");

        // 1. Resolve the latest mod release (pinned fallback).
        progress.Report((6, "Checking the latest Caves of Qud Archipelago mod release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the Caves of Qud Archipelago mod download on GitHub. " +
                "Check your internet connection, or download the mod manually from " +
                ModRepoUrl + "/releases and extract it to your Mods folder.");

        // 2. Download + extract the mod zip INTO <CoQ>/Mods/.
        string modsDir = Path.Combine(coqDir, "Mods");
        await DownloadAndExtractModAsync(zipUrl, version, modsDir, progress, ct);

        // 3. Stamp the version.
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool dllOk = FindInstalledModDll() != null;
        progress.Report((100,
            $"Installed the Caves of Qud Archipelago mod {version} into your Mods folder" +
            (dllOk ? "." : " (verify the files landed).") +
            " To play: launch Caves of Qud and connect to your Archipelago server " +
            "in-game. (This launcher cannot pre-fill the connection — it is entered " +
            "in-game via the mod's interface.)"));
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
        // The AP server connection for Caves of Qud is entered in-game via the mod's
        // interface. There is no documented command-line / config prefill mechanism.
        // Launching from this tile just starts the game; the user connects in-game.
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on this
        // slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartCoq();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) Caves of Qud runs perfectly well.
    public bool SupportsStandalone => true;

    /// The mod owns the slot connection.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartCoq();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The mod receives items from the AP server directly; nothing for the launcher
        // to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// A valid Caves of Qud folder contains CoQ.exe. Return null when acceptable,
    /// else a short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Caves of Qud install folder.";

        if (LooksLikeCoqDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeCoqDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Caves of Qud installation. Pick the folder " +
               "that contains CoQ.exe (for Steam this is usually " +
               @"...\steamapps\common\Caves of Qud).";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Caves of Qud is your own game (Steam) with the Archipelago randomizer " +
                   "mod by lonesurv1vor added on top. The launcher detects your Steam install " +
                   "and downloads the mod into your Mods folder. You connect to your server " +
                   "in-game via the mod's interface. The AP world name is unverified offline " +
                   "— verify against worlds/__init__.py when generating.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CAVES OF QUD INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? coqDir      = ResolveCoqDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg = coqDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + coqDir
                : "Detected Steam install: " + coqDir)
            : "Caves of Qud not detected. Pick your install folder below, or install " +
              "Caves of Qud via Steam first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = coqDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // mod status line
        string? modDll = FindInstalledModDll();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                    ? "Archipelago mod found: " + modDll
                    : "Archipelago mod not found in Mods/ yet. Use Install on the Play tab, " +
                      "or download the mod manually from the GitHub repo (see links below).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? coqDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Caves of Qud install folder (the one containing CoQ.exe). " +
                          "Detected from Steam automatically; set it here to override.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120, Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Caves of Qud install folder (contains CoQ.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? coqDir ?? "")
                                   ? (overrideDir ?? coqDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Caves of Qud folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeCoqDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeCoqDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 333640). Use this " +
                   "picker for a non-standard Steam library location.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection ───────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in-game)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Launch Caves of Qud with the mod installed. Use the mod's in-game " +
                   "interface to enter your Archipelago server address, port, slot name, " +
                   "and password (if any), then connect. This launcher does not pre-fill " +
                   "the connection.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "SETUP STEPS", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Caves of Qud (Steam, appid 333640). Install it if you have not. Use " +
                "the folder picker above if it was not detected.",
            "2. Click Install on the Play tab. The launcher downloads the mod from " +
                "lonesurv1vor/CavesOfQudArchipelagoRandomizer and extracts it to your " +
                "Mods/ folder. Alternatively download from the GitHub releases page (link " +
                "below) and extract to <Caves of Qud>/Mods/ yourself.",
            "3. Launch Caves of Qud and confirm the mod loads (check the mod menu or the " +
                "main screen for an Archipelago option).",
            "4. To play: use the mod's in-game connection interface to enter your " +
                "Archipelago server address, port, slot name, and password (if any), then " +
                "connect and start your run.",
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
            ("Caves of Qud Archipelago Mod (GitHub) ↗", ModRepoUrl),
            ("Mod Releases (download) ↗",               ModRepoUrl + "/releases"),
            ("Caves of Qud AP Guide ↗",                 SetupGuideUrl),
            ("Archipelago Official ↗",                  ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label, HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new System.Windows.Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0), FontSize = 12,
                Margin = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) => { try { Process.Start(
                new ProcessStartInfo(u) { UseShellExecute = true }); } catch { } };
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
            using var doc  = JsonDocument.Parse(json);
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

    /// "v0.1.0" → "0.1.0" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + a .zip download URL. Finds the
    /// first .zip asset on the latest release; falls back to the pinned version
    /// direct URL when the API is unreachable.
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
                string? zipUrl = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        zipUrl = url;
                        break;
                    }
                }
                if (zipUrl != null)
                    return (version, zipUrl);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        // Pinned fallback: construct a direct URL for the expected first release.
        string fallbackUrl =
            $"{ModRepoUrl}/releases/download/v{FallbackVersion}/CavesOfQudAP-v{FallbackVersion}.zip";
        return (FallbackVersion, fallbackUrl);
    }

    // ── Private helpers — Steam / Caves of Qud detection ─────────────────────

    /// The CoQ install dir to use: the override (if set and valid) wins, else the
    /// Steam-detected install. Null when nothing is found.
    private string? ResolveCoqDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeCoqDir(ov))
            return ov;

        try { return DetectSteamCoqDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Caves of Qud if it has CoQ.exe in it.
    private static bool LooksLikeCoqDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, CoqExeName))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam Caves of Qud install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_333640.acf exists → steamapps\common\Caves of Qud.
    private static string? DetectSteamCoqDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{CoqAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeCoqDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeCoqDir(conventional)) return conventional;
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    /// Candidate Steam install roots from the registry (HKCU SteamPath +
    /// HKLM WOW6432Node InstallPath). Both are tried; duplicates are harmless.
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

        // Last-ditch conventional location.
        string? progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

    /// Steam stores its SteamPath with forward slashes; normalize for Path APIs.
    private static string NormalizeSteamPath(string p)
        => p.Replace('/', '\\').TrimEnd('\\');

    /// All Steam library roots: the Steam root itself plus every "path" entry in
    /// steamapps\libraryfolders.vdf. Tolerant text scan.
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

    /// Pull every  "path"   "<value>"  pair out of a libraryfolders.vdf body.
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

    /// Read the "installdir" value from an appmanifest_*.acf.
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

    /// Safe registry string read; null on any failure.
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

    /// Find any .dll whose filename contains "archipelago" (case-insensitive) under
    /// <CoQ>/Mods/ (recursive). Returns the first match or null.
    private string? FindInstalledModDll()
    {
        try
        {
            string? coq = ResolveCoqDir();
            if (coq == null) return null;
            string modsDir = Path.Combine(coq, "Mods");
            if (!Directory.Exists(modsDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(modsDir, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(dll);
                if (name.IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                    return dll;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start Caves of Qud: prefer CoQ.exe in the detected/override install; fall
    /// back to the steam:// URL if Steam is present.
    private void StartCoq()
    {
        string? coq = ResolveCoqDir();
        string? exe = coq != null ? Path.Combine(coq, CoqExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = coq!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Caves of Qud.");

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

        // Fall back to Steam if we at least know Steam is installed.
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
            "Could not find CoQ.exe. Open this game's Settings and pick your " +
            "Caves of Qud install folder, or install Caves of Qud via Steam.",
            CoqExeName);
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the mod zip and extract it into <CoQ>/Mods/. The zip structure is
    /// preserved so the mod's own subfolder name is respected (e.g. Mods/CavesOfQudAP/).
    /// Existing files are overwritten; other mod subfolders under Mods/ are not disturbed.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string modsDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"caves-of-qud-ap-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"caves-of-qud-ap-x-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading Caves of Qud Archipelago mod {version}..."));
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
                        int pct = (int)(10 + 60 * downloaded / total);
                        progress.Report((pct, $"Downloading Caves of Qud Archipelago mod... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((75, "Installing the mod into your Mods folder..."));
            Directory.CreateDirectory(modsDir);
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // Merge the extracted tree INTO the Mods/ folder. Preserve the mod's
            // own subfolder structure so Qud's mod loader can find it.
            MergeDirectory(tempExtract, modsDir);

            progress.Report((90, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    /// Recursively copy a directory tree INTO an existing destination, overwriting
    /// individual files but preserving any sibling files already there.
    private static void MergeDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceDir, file);
            string dst = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            try { File.Copy(file, dst, overwrite: true); }
            catch { /* locked file (game open?) — skip; user can retry with game closed */ }
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class CoqSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private CoqSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<CoqSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(CoqSettings s)
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
