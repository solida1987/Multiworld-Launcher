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

namespace LauncherV2.Plugins.IntoTheBreach;

// ═══════════════════════════════════════════════════════════════════════════════
// IntoTheBreachPlugin — install / launch for "Into the Breach" (Subset Games,
// 2018) played through the ITB-randomizer-for-AP mod by Ishigh1, which bundles
// its own lua-apclientpp.dll so the game speaks directly to the AP server.
//
// ── CONFIRMED FACTS (2026-06-15, verified against repo + releases) ─────────────
//
//   * MOD REPO: Ishigh1/ITB-randomizer-for-AP
//     https://github.com/Ishigh1/ITB-randomizer-for-AP
//     Latest release (2026-06-15): v0.15.18
//
//   * AP GAME NAME is "Into the Breach" — verified in
//     ap/ap_link.lua line ~396:  game_name = "Into the Breach"
//
//   * RELEASE ASSETS (consistent across all recent releases, confirmed):
//     - into_the_breach.apworld  — the apworld for hosting/generating (not needed in-game)
//     - itb_apworld_and_dependencies.7z — full bundle (apworld + randomizer mod)
//     - randomizer.7z            — the in-game Lua mod only (what goes in ITB's mods/)
//
//   * MOD INSTALL TARGET: the mod folder is named "randomizer" (id = "randomizer"
//     in init.lua). It is placed at <ITBDir>/mods/randomizer/. The mod requires
//     Into the Breach's own mod framework (modApiVersion = "2.9.3"); the game
//     ships this natively (no separate install step).
//
//   * CONNECTION IS IN-GAME: when ITB launches with the randomizer mod active,
//     a pop-up at the main menu asks for Server address, Slot name, and Password.
//     The mod (via lua-apclientpp.dll) connects to the AP server itself. This
//     launcher cannot pre-fill those fields — there is no config file or command-
//     line argument for it (verified in ap/ap_ui.lua: it is a UiInputField dialog).
//     ConnectsItself = true.
//
//   * STEAM APPID: 590380 (Into the Breach).
//     Default Steam install path: steamapps/common/Into The Breach/
//     The game executable is "Into The Breach.exe".
//
//   * MOD ACTIVATION: Into the Breach has a built-in mod manager (press F2 at the
//     main menu, or via game settings). The randomizer mod must be enabled there
//     before play. The launcher cannot enable it automatically — the human must
//     enable it in-game once after install.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Into the Breach install via the Windows registry
//      (HKCU\Software\Valve\Steam SteamPath + HKLM WOW6432Node InstallPath),
//      parsing steamapps\libraryfolders.vdf for all library roots and locating
//      steamapps\common\Into The Breach via appmanifest_590380.acf. A manual
//      install-dir override (folder picker in Settings) is also supported and
//      takes precedence; it is validated (must contain "Into The Breach.exe")
//      and persisted in this plugin's OWN sidecar JSON — Core/SettingsStore is
//      not modified.
//   2. INSTALL/UPDATE: download randomizer.7z from the latest GitHub release and
//      extract its contents into <ITBDir>/mods/randomizer/. Because the zip
//      bundles lua-apclientpp.dll and all other dependencies, this is a COMPLETE
//      mod install. A clear numbered guide then surfaces the remaining in-game
//      steps (enable the mod in the mod manager, re-enter the main menu, and fill
//      in the AP credentials in the pop-up dialog).
//      NOTE: randomizer.7z is a 7-Zip archive. The launcher uses SharpCompress
//      via a bundled copy for extraction, which is already a dependency of the
//      launcher project.
//   3. LAUNCH: run "Into The Breach.exe" from the detected/override install dir.
//      SupportsStandalone = true (the base game runs without any AP mod active).
//      ConnectsItself = true (the Lua mod owns the slot; the launcher must NOT
//      hold its own ApClient on the same slot while the game runs).
//
// ── ARCHITECTURE NOTE ──────────────────────────────────────────────────────────
//   * 7z extraction: Into the Breach's mod releases use 7-Zip (.7z) archives, not
//     zip. This plugin therefore uses Core/ArchiveExtractor.Extract(), which delegates
//     to Windows' built-in bsdtar (libarchive+liblzma, ships in Win 10 1803+ / Win 11)
//     for .7z. No NuGet dependency is added — the same approach used by the emulator
//     plugins (snes9x-emunwa, etc.).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class IntoTheBreachPlugin : IGamePlugin
{
    // ── Constants — the ITB-randomizer-for-AP mod (real repo, verified 2026-06-15)
    private const string MOD_OWNER   = "Ishigh1";
    private const string MOD_REPO    = "ITB-randomizer-for-AP";
    private const string ModRepoUrl  = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    // Steam — Into the Breach appid 590380.
    private const string ItbSteamAppId        = "590380";
    private static readonly string SteamRunUrl = $"steam://rungameid/{ItbSteamAppId}";

    /// The Steam steamapps/common/ sub-folder name for Into the Breach.
    private const string SteamCommonFolderName = "Into The Breach";

    /// The main game executable.
    private const string ItbExeName = "Into The Breach.exe";

    /// The mod folder placed under <ITBDir>/mods/ (the mod's id field in init.lua).
    private const string ModFolderName = "randomizer";

    /// The 7z asset name in every release (verified across v0.15.16–v0.15.18).
    private const string ModAssetName = "randomizer.7z";

    /// Pinned fallback when the GitHub API is unreachable. Verified 2026-06-15.
    private const string FallbackVersion    = "0.15.18";
    private const string FallbackAssetUrl   =
        $"{ModRepoUrl}/releases/download/v{FallbackVersion}/{ModAssetName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ─────────────────────────────────────────────────

    public string GameId      => "into_the_breach";
    public string DisplayName => "Into the Breach";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game name — verified in ap/ap_link.lua:
    ///   game_name = "Into the Breach"
    public string ApWorldName => "Into the Breach";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "into_the_breach.png");

    public string ThemeAccentColor => "#1A3A5C";   // squad-blue / mech steel
    public string[] GameBadges     => new[] { "Steam · needs mod" };

    public string Description =>
        "Into the Breach, Subset Games' acclaimed turn-based tactics game, played " +
        "through the ITB-randomizer-for-AP mod by Ishigh1. The mod bundles its own " +
        "Archipelago client (lua-apclientpp) so the game connects to the multiworld " +
        "directly — no emulator, no bridge. Squad unlocks, upgrades, achievements, " +
        "and island clears become checks shuffled across the multiworld. You bring " +
        "your own copy of Into the Breach (owned on Steam); the launcher installs " +
        "the randomizer mod for you. After install, enable the mod in the in-game " +
        "mod manager (F2), then enter your server credentials in the pop-up dialog " +
        "that appears at the main menu.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ──────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the randomizer mod folder is present in a detected/override
    /// ITB install's mods tree (holds init.lua). Honors hand-installations.
    public bool IsInstalled => FindInstalledModFolder() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ──────────────────────────────────────────────────────────────────

    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "IntoTheBreach");

    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "into_the_breach_launcher.json");

    // ── Internal state ─────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ───────────────────────────────────────────────────────
    // The randomizer mod talks to the AP server itself — the launcher relays nothing.
    // These exist for interface compatibility (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── Lifecycle — CheckForUpdate ─────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            InstalledVersion = FindInstalledModFolder() != null
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

    // ── Lifecycle — InstallOrUpdate ────────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((2, "Locating your Into the Breach installation..."));
        string? itbDir = ResolveItbDir();
        if (itbDir == null)
            throw new InvalidOperationException(
                "Could not find an Into the Breach installation. Open this game's " +
                "Settings and pick your Into the Breach folder (the one containing " +
                "\"Into The Breach.exe\"), or install the game via Steam first.");

        string modsDir  = Path.Combine(itbDir, "mods");
        string modDir   = Path.Combine(modsDir, ModFolderName);

        progress.Report((6, "Checking the latest ITB-randomizer-for-AP release..."));
        var (version, assetUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        if (assetUrl == null)
            throw new InvalidOperationException(
                "Could not find the randomizer.7z download on GitHub. Check your " +
                "internet connection, or download it manually from " +
                ModRepoUrl + "/releases. Extract the contents into a folder named " +
                "\"randomizer\" inside Into the Breach's \"mods\" folder.");

        await DownloadAndExtractModAsync(assetUrl, version, modDir, progress, ct);

        WriteStampedVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            $"Installed the randomizer mod {version} into mods\\randomizer. " +
            "NEXT STEPS: launch Into the Breach, press F2 (or open the in-game " +
            "mod manager) and enable the \"Randomizer\" mod, then return to the " +
            "main menu. A dialog will appear asking for your AP server address, " +
            "slot name, and password — fill those in and the mod connects. " +
            "(Open Settings for the full guided steps.)"));
    }

    // ── Lifecycle — Verify ─────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // ── Lifecycle — Launch ─────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // HONEST: the AP server connection is entered in-game via a pop-up dialog
        // that the randomizer mod shows at the main menu (verified in ap/ap_ui.lua:
        // it uses UiInputField — there is no CLI/config prefill path). ConnectsItself
        // = true: the Lua mod owns the slot; the launcher must NOT hold its own
        // ApClient on the same slot while the game is running.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartItb();
        return Task.CompletedTask;
    }

    /// Plain Into the Breach runs fine without the AP mod active.
    public bool SupportsStandalone => true;

    /// The randomizer mod (via lua-apclientpp.dll) owns the AP slot connection.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartItb();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ──────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask; // the mod receives items from the AP server directly

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status indicator in-game.
    }

    // ── Existing-install validation (folder picker) ────────────────────────────

    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Into the Breach install folder.";

        if (LooksLikeItbDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeItbDir(nested))
                return null;
        }
        catch { }

        return "That does not look like an Into the Breach installation. Pick the " +
               "folder that contains \"Into The Breach.exe\" " +
               @"(for Steam this is usually ...\steamapps\common\Into The Breach).";
    }

    // ── Settings UI ────────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Honest honesty header ──────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "Into the Breach is your own game (Steam) with the ITB-randomizer-for-AP " +
                   "mod added on top into the game's own mods folder. The launcher can detect " +
                   "your Steam install and install the randomizer mod for you. Two things are " +
                   "still done in-game: you must enable the \"Randomizer\" mod in the in-game " +
                   "mod manager (F2 at the main menu), and you enter your AP server / slot / " +
                   "password in the dialog the mod shows when the main menu first loads. These " +
                   "steps are not automated by this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ───────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "INTO THE BREACH INSTALL", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? itbDir      = ResolveItbDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg   = itbDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + itbDir
                : "Detected Steam install: " + itbDir)
            : "Into the Breach not detected. Pick your install folder below, or " +
              "install it via Steam first.";
        panel.Children.Add(new TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = itbDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // mod status
        string? modFolder = FindInstalledModFolder();
        panel.Children.Add(new TextBlock
        {
            Text = modFolder != null
                ? "Randomizer mod found: " + modFolder
                : "Randomizer mod not found in your mods folder yet (use Install on the " +
                  "Play tab, or install it by hand from the mod releases).",
            FontSize = 11, Foreground = modFolder != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text = overrideDir ?? itbDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Into the Breach install folder (the one containing " +
                          "\"Into The Breach.exe\"). Detected from Steam automatically; " +
                          "set it here for a non-standard Steam library or another store.",
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
                Title            = "Select your Into the Breach install folder (contains \"Into The Breach.exe\")",
                InitialDirectory = Directory.Exists(overrideDir ?? itbDir ?? "")
                                   ? (overrideDir ?? itbDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not an Into the Breach folder",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend.
                if (!LooksLikeItbDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeItbDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 590380). Use this " +
                   "picker for a non-standard Steam library or another store.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection (in-game) ──────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "CONNECTING (entered in-game)", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "When Into the Breach launches with the Randomizer mod active, a " +
                   "dialog appears at the main menu asking for Server address, Slot name, " +
                   "and Password. Enter your AP server credentials there — the mod connects " +
                   "directly to the AP server (no launcher-side client is held on the slot). " +
                   "This launcher does not pre-fill the connection dialog.",
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
            "1. Own Into the Breach (on Steam). Install it if you have not already. Use " +
                "the folder picker above if it was not auto-detected.",
            "2. Install the randomizer mod: click Install on the Play tab — the launcher " +
                "downloads randomizer.7z from the mod's releases and extracts it into your " +
                "Into the Breach mods\\randomizer folder. Or do it by hand: create a folder " +
                "named \"randomizer\" in the game's \"mods\" folder and extract the " +
                "randomizer.7z contents into it.",
            "3. Launch Into the Breach. At the main menu, open the in-game mod manager " +
                "(press F2 or go to Settings -> Mods) and enable the \"Randomizer\" mod. " +
                "Apply and return to the main menu.",
            "4. A dialog appears asking for Server address, Slot name, and Password. " +
                "Enter your AP server credentials (e.g. archipelago.gg:38281, your slot, " +
                "and password if any) and click Connect.",
            "5. The mod reports \"Connected\" in the dialog. You are now in the multiworld. " +
                "Start a new run — checks are sent and items received during play.",
            "6. The apworld file (into_the_breach.apworld) is also in the release for " +
                "hosting / generating seeds. Install it in your Archipelago server's " +
                "custom_worlds folder when generating a multiworld that includes ITB.",
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
        foreach (var (label, url) in new (string, string)[]
        {
            ("ITB-randomizer-for-AP (GitHub) ↗", ModRepoUrl),
            ("Releases ↗",                       $"{ModRepoUrl}/releases"),
            ("Archipelago Official ↗",           "https://archipelago.gg"),
        })
        {
            var btn = new Button
            {
                Content = label, HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), FontSize = 12, Margin = new Thickness(0, 0, 0, 4),
                Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) => { try { Process.Start(
                new ProcessStartInfo(u) { UseShellExecute = true }); } catch { } };
            panel.Children.Add(btn);
        }
        return panel;
    }

    // ── News feed ──────────────────────────────────────────────────────────────

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
                if (el.TryGetProperty("published_at", out var d) && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : "",
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: el.TryGetProperty("tag_name", out var t) ? NormalizeTag(t.GetString()) ?? "" : "",
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var u) ? u.GetString() : null
                ));
                if (items.Count >= 20) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — release resolution ───────────────────────────────────

    /// "v0.15.18" → "0.15.18" (strips leading 'v' before a digit).
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest release: version string + download URL for randomizer.7z.
    /// Falls back to the pinned v0.15.18 direct URL on API failure.
    private async Task<(string Version, string? AssetUrl)> ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? sevenZip = null; // the randomizer.7z we want
                string? anyAsset = null; // any .7z as last resort
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    // Prefer "randomizer.7z" exactly (verified asset name).
                    if (lower == ModAssetName.ToLowerInvariant())
                    {
                        sevenZip = url;
                        break;
                    }
                    // Secondary: any .7z that contains "randomizer" (not the apworld bundle)
                    if (lower.EndsWith(".7z") && lower.Contains("randomizer"))
                        sevenZip ??= url;
                    // Tertiary: any .7z
                    if (lower.EndsWith(".7z"))
                        anyAsset ??= url;
                }
                string? chosen = sevenZip ?? anyAsset;
                if (chosen != null)
                    return (version, chosen);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (FallbackVersion, FallbackAssetUrl);
    }

    // ── Private helpers — Steam / ITB detection ────────────────────────────────

    private string? ResolveItbDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeItbDir(ov))
            return ov;

        try { return DetectSteamItbDir(); }
        catch { return null; }
    }

    private static bool LooksLikeItbDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, ItbExeName))) return true;
            // Secondary signal: the game's data folder.
            if (Directory.Exists(Path.Combine(dir, "resources"))) return true;
            return false;
        }
        catch { return false; }
    }

    private static string? DetectSteamItbDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{ItbSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common    = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeItbDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeItbDir(conventional)) return conventional;
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

        string? hklm64 = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm64)) yield return NormalizeSteamPath(hklm64);

        string? progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

    private static string NormalizeSteamPath(string p) => p.Replace('/', '\\').TrimEnd('\\');

    private static IEnumerable<string> SteamLibraryRoots(string steamRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (seen.Add(steamRoot)) yield return steamRoot;

        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        string text;
        try { text = File.ReadAllText(vdf); }
        catch { yield break; }

        foreach (string p in ExtractVdfPaths(text))
        {
            string norm = p.Replace('/', '\\').TrimEnd('\\');
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
            string text  = File.ReadAllText(acfPath);
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

    // ── Private helpers — installed mod detection ──────────────────────────────

    private string? FindInstalledModFolder()
    {
        try
        {
            string? itb = ResolveItbDir();
            if (itb == null) return null;
            string modsDir = Path.Combine(itb, "mods");
            if (!Directory.Exists(modsDir)) return null;

            // Canonical: mods/randomizer containing init.lua (the mod entry point).
            string canonical = Path.Combine(modsDir, ModFolderName);
            if (Directory.Exists(canonical) && LooksLikeModFolder(canonical))
                return canonical;

            // Defensive scan: any mods sub-folder whose init.lua mentions Archipelago.
            foreach (string sub in Directory.EnumerateDirectories(modsDir))
            {
                if (Path.GetFileName(sub).Equals(ModFolderName, StringComparison.OrdinalIgnoreCase))
                    return sub;

                string initLua = Path.Combine(sub, "init.lua");
                if (!File.Exists(initLua)) continue;
                try
                {
                    string txt = File.ReadAllText(initLua);
                    if (txt.Contains("ap_link", StringComparison.OrdinalIgnoreCase) ||
                        txt.Contains("archipelago", StringComparison.OrdinalIgnoreCase))
                        return sub;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private static bool LooksLikeModFolder(string dir)
    {
        try
        {
            return File.Exists(Path.Combine(dir, "init.lua")) ||
                   File.Exists(Path.Combine(dir, "ap_link.lua")) ||
                   Directory.Exists(Path.Combine(dir, "ap"));
        }
        catch { return false; }
    }

    // ── Private helpers — launch ───────────────────────────────────────────────

    private void StartItb()
    {
        string? itb = ResolveItbDir();
        string? exe = itb != null ? Path.Combine(itb, ItbExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = itb!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Into the Breach.");

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

        // Fall back to Steam if we know Steam is present.
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
            "Could not find \"Into The Breach.exe\". Open this game's Settings and " +
            "pick your Into the Breach install folder, or install it via Steam.",
            ItbExeName);
    }

    // ── Private helpers — download and extract the mod (.7z) ──────────────────

    /// Download randomizer.7z and extract into <ITBDir>/mods/randomizer/.
    /// Uses SharpCompress for 7z support (already a launcher project dependency).
    private async Task DownloadAndExtractModAsync(
        string assetUrl,
        string version,
        string modDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempFile = Path.Combine(Path.GetTempPath(),
            $"itb-randomizer-{version}-{Guid.NewGuid():N}.7z");
        try
        {
            progress.Report((10, $"Downloading randomizer mod {version}..."));
            using var response = await _http.GetAsync(
                assetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long total      = response.Content.Headers.ContentLength ?? -1;
            long downloaded = 0;

            await using (var src = await response.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tempFile))
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
                        progress.Report((pct, $"Downloading randomizer mod... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((75, "Installing the mod into your Into the Breach mods folder..."));

            // Replace any existing mod folder so an update is clean.
            if (Directory.Exists(modDir))
            {
                try { Directory.Delete(modDir, recursive: true); } catch { }
            }
            Directory.CreateDirectory(modDir);

            // Extract the 7z into the mod directory via SharpCompress.
            ExtractSevenZip(tempFile, modDir);

            // Defensive: if the 7z wrapped everything in a single sub-folder, flatten it.
            if (!LooksLikeModFolder(modDir))
            {
                string[] subdirs = Directory.GetDirectories(modDir);
                if (subdirs.Length == 1 && LooksLikeModFolder(subdirs[0]))
                {
                    string sub = subdirs[0];
                    foreach (string fileSrc in Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories))
                    {
                        string rel     = Path.GetRelativePath(sub, fileSrc);
                        string fileDst = Path.Combine(modDir, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(fileDst)!);
                        File.Move(fileSrc, fileDst, overwrite: true);
                    }
                    try { Directory.Delete(sub, recursive: true); } catch { }
                }
            }

            progress.Report((90, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    /// Extract a .7z archive to the given output directory via ArchiveExtractor,
    /// which uses the Windows-bundled bsdtar (libarchive+liblzma — reads .7z natively).
    private static void ExtractSevenZip(string archivePath, string outputDir)
    {
        // ArchiveExtractor.Extract dispatches on extension: .7z → bsdtar, .zip → ZipFile.
        // bsdtar ships in Windows 10 1803+ and all Windows 11 — no extra dependency.
        try
        {
            ArchiveExtractor.Extract(archivePath, outputDir);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to extract randomizer.7z. Make sure you are on Windows 10 1803+ " +
                "or Windows 11 (bsdtar is required). If the error persists, extract the " +
                "archive manually with 7-Zip into your Into the Breach mods\\randomizer " +
                "folder. Error: " + ex.Message, ex);
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class ItbSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private ItbSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<ItbSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(ItbSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
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
    private void SaveOverrideDir(string p) { var s = LoadSettings(); s.InstallOverride = p; SaveSettings(s); }

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().ModVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
    private void WriteStampedVersion(string v) { var s = LoadSettings(); s.ModVersion = v; SaveSettings(s); }
}
