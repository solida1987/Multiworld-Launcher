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

namespace LauncherV2.Plugins.Reventure;

// ═══════════════════════════════════════════════════════════════════════════════
// ReventurePlugin — install / launch for "Reventure" (Pixelatto, 2019)
// played through the ReventureEndingRando mod by Droppel, which contains the
// in-game Archipelago Multiworld client. This is a NATIVE "ConnectsItself"
// integration in the same family as the shipped Hollow Knight / Timespinner /
// TUNIC / Stardew Valley plugins: the game itself speaks to the AP server
// (no emulator, no Lua bridge, no launcher-held ApClient on the slot).
//
// ── WHAT IS REVENTURE? ────────────────────────────────────────────────────────
// Reventure is a 100-ending 2D pixel-art platformer / adventure by Pixelatto
// (Steam AppID 900270, released 2019). Players explore a fairy-tale world
// collecting items, pulling switches, and triggering all 100 unique endings.
// The Archipelago randomizer shuffles endings across the multiworld: each ending
// you trigger is a location check, and items you receive unlock new mechanics.
//
// ── REALITY CHECK (2026-06-14, research this session) ─────────────────────────
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of Reventure (Steam AppID 900270), and Archipelago support is delivered as the
// ReventureEndingRando mod by Droppel added on top. The honest integration ceiling
// — exactly like the shipped Hollow Knight / Timespinner plugins — is "automate
// what is possible, guide the irreducible parts." The researched facts:
//
//   * THE AP WORLD game string is "Reventure" (AP world
//     Droppel/ReventureEndingRando). GameId here = "reventure".
//
//   * THE MOD repo is Droppel/ReventureEndingRando (GitHub). This is the only
//     known Reventure Archipelago mod. Releases are standard (non-prerelease)
//     GitHub releases. The mod is likely a BepInEx plugin (common for Unity
//     games) — the exact mod layout should be verified against the release zip
//     and any README. This plugin extracts the mod zip into the Reventure install
//     directory and flattens single-subdir wrapping as needed.
//
//   * INSTALL APPROACH: Download the release zip from Droppel/ReventureEndingRando
//     /releases, extract into the Reventure Steam install directory. Connection
//     details (server / slot / password) are entered in-game or via any config
//     file the mod provides. This plugin surfaces the connection values in the
//     Settings panel for the user to copy in-game.
//
//   * LAUNCH: run Reventure.exe from the detected Steam install, or fall back to
//     steam://rungameid/900270. ConnectsItself = true: the mod owns the slot —
//     the launcher must NOT hold its own ApClient on it.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Reventure install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, and
//      HKLM\...\WOW6432Node\Valve\Steam -> InstallPath), parsing
//      steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\Reventure via appmanifest_900270.acf. A manual
//      install-dir OVERRIDE (settings folder picker) is also supported and takes
//      precedence; it is validated (must contain Reventure.exe) and persisted in
//      this plugin's OWN sidecar (Games/ROMs/reventure/reventure_launcher.json)
//      — Core/SettingsStore is NOT modified.
//   2. INSTALL/UPDATE (best effort) = download the release zip from GitHub and
//      extract it INTO the detected Reventure folder. The settings panel provides
//      clear guided steps + links (mod repo, archipelago.gg) so the user can
//      complete any remaining setup.
//   3. LAUNCH = run Reventure.exe from the detected/override install (with the mod
//      present). ConnectsItself = true (the mod owns the slot). SupportsStandalone
//      = true (plain Reventure runs fine without AP). Connection is entered in-game
//      (the mod provides its own in-game or config-file connection mechanism).
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", Hollow-Knight-style) ───────
//   * The exact mod release layout (zip structure, exe name inside, BepInEx vs
//     patcher) was not confirmed offline. ResolveGameExe() looks for Reventure.exe
//     in the detected install. The mod's "installed" detection looks for a mod DLL
//     or marker file matching "reventure" in the install (case-insensitive).
//   * Steam library parsing is defensive: a hand-written tolerant VDF scan that
//     pulls quoted "path" values; any failure degrades to "not found" rather than
//     throwing. Falls back to steam://rungameid/900270 when no exe is found.
//   * No plaintext AP password is ever written to disk by this plugin (the
//     connection is entered in-game), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ReventurePlugin : IGamePlugin
{
    // ── Constants — the Droppel mod (GitHub) ──────────────────────────────────
    private const string MOD_OWNER  = "Droppel";
    private const string MOD_REPO   = "ReventureEndingRando";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string SetupGuideUrl   = "https://archipelago.gg/tutorial/Reventure/setup/en";
    private const string GameInfoUrl     = "https://archipelago.gg/games/Reventure/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Reventure AppID 900270.
    private const string ReventureSteamAppId    = "900270";
    private static readonly string SteamRunUrl  = $"steam://rungameid/{ReventureSteamAppId}";

    /// The conventional Steam steamapps/common folder name for Reventure.
    private const string SteamCommonFolderName = "Reventure";

    /// The base-game executable we look for when detecting the install.
    private const string BaseExeName = "Reventure.exe";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "reventure";
    public string DisplayName => "Reventure";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — matches the Droppel/ReventureEndingRando AP world.
    public string ApWorldName => "Reventure";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "reventure.png");

    public string ThemeAccentColor => "#D4A017";   // golden fairy-tale pixel art

    public string[] GameBadges => new[] { "Steam · needs mod" };

    public string Description =>
        "Reventure, Pixelatto's 100-ending 2D pixel-art platformer/adventure, played " +
        "through the ReventureEndingRando mod by Droppel — an in-game Archipelago " +
        "client. Every ending you trigger is a location check, and items received " +
        "unlock new mechanics and areas across the multiworld. The game connects to " +
        "the Archipelago server itself (no emulator, no bridge). You bring your own " +
        "copy of Reventure (Steam AppID 900270); the mod is added on top. The launcher " +
        "detects your Steam install, can stage the mod files, and guides the rest. " +
        "You connect to your server from the in-game Archipelago menu.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the mod DLL / marker file is present in a detected/
    /// override Reventure install. We do NOT gate on our own stamp — the user may
    /// have installed the mod by hand, which we honor.
    public bool IsInstalled => FindInstalledModFile() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads and any working files. The actual mod is
    /// extracted INTO the Reventure install folder, not here. Exposed as
    /// GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Reventure");

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained file — same as Hollow Knight /
    /// Timespinner / TUNIC / Jak). Lives under Games/ROMs/reventure/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "reventure_launcher.json");

    /// Version stamp written next to our sidecar after a direct-zip install.
    private string VersionStampPath
        => Path.Combine(SettingsSidecarDir, "reventure_mod_version.dat");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The ReventureEndingRando mod reports checks/items/goal to the AP server
    // itself — the launcher relays nothing. These exist for interface
    // compatibility (ConnectsItself = true).
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
            // Best-effort: read the version we stamped after a direct install if
            // present; otherwise report "installed" when any mod file exists.
            InstalledVersion = FindInstalledModFile() != null
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
        // 0. We need a Reventure install to drop the mod into. Prefer an explicit
        //    override; else auto-detect the Steam install.
        progress.Report((2, "Locating your Reventure installation..."));
        string? rvDir = ResolveReventureDir();
        if (rvDir == null)
            throw new InvalidOperationException(
                "Could not find a Reventure installation. Open this game's Settings " +
                "and pick your Reventure folder (the one containing Reventure.exe), " +
                "or install Reventure via Steam first (AppID 900270). The Archipelago " +
                "mod is added on top of your own copy of the game.");

        // 1. Resolve the latest mod release (falls back gracefully if API is down).
        progress.Report((6, "Checking the latest ReventureEndingRando release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the ReventureEndingRando download on GitHub. Check " +
                "your internet connection, or download it manually from " +
                ModRepoUrl + "/releases — see Settings for the guided steps.");

        // 2. Download + extract the mod zip INTO the Reventure install folder.
        await DownloadAndExtractModAsync(zipUrl, version, rvDir, progress, ct);

        // 3. Stamp the version next to our sidecar so the tile can show it.
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool ok = FindInstalledModFile() != null;
        progress.Report((100,
            ok
                ? $"Staged ReventureEndingRando {version} into your Reventure folder. " +
                  "To play: press Play, then connect to your Archipelago server from " +
                  "the in-game menu. Open Settings for guided steps and links."
                : $"Downloaded ReventureEndingRando {version}, but the mod was not found " +
                  "in your Reventure folder afterwards. Confirm the install folder in " +
                  "Settings, or extract the release zip into it by hand."));
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
        // HONEST: the AP server connection for Reventure is entered in the
        // IN-GAME Archipelago menu (or via any config file the mod provides).
        // There is no documented command-line / config prefill this launcher can
        // reliably apply without verifying the mod's exact interface. So launching
        // from this tile just starts the game; the user connects in-game with the
        // session credentials (the settings panel surfaces those values to copy).
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism verified
        StartReventure();
        return Task.CompletedTask;
    }

    /// Reventure is a complete game — plain (non-AP) play is supported.
    public bool SupportsStandalone => true;

    /// The ReventureEndingRando mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    public string? BuiltAgainstDataPackageChecksum => null;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        // Plain Reventure without AP — just run the exe.
        StartReventure();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        // No plaintext AP password is ever written to disk by this plugin (the
        // connection is entered in-game), so there is nothing to scrub.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The mod receives items from the AP server directly; nothing to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game; no launcher HUD channel.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var gold    = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xD4, 0xA0, 0x17));

        var panel = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20)
        };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Reventure is your own game (Steam AppID 900270) with the " +
                   "ReventureEndingRando mod by Droppel added on top. The launcher " +
                   "detects your Steam install and can extract the mod into it. " +
                   "You connect to your Archipelago server from in-game — see the " +
                   "guided steps below. These external steps are not verified by " +
                   "this launcher.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "REVENTURE INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? rvDir       = ResolveReventureDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg   = rvDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + rvDir
                : "Detected Steam install: " + rvDir)
            : "Reventure not detected. Pick your install folder below, or install " +
              "Reventure via Steam (AppID 900270) first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = rvDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // Mod status line
        string? modFile = FindInstalledModFile();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modFile != null
                ? "ReventureEndingRando mod found: " + modFile
                : "ReventureEndingRando mod not found yet. Use Install on the Play tab, " +
                  "or extract the release zip into your Reventure folder by hand.",
            FontSize = 11, Foreground = modFile != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4)
        };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? rvDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip = "Your Reventure install folder (the one containing Reventure.exe). " +
                      "Detected from Steam automatically; set it here to override.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Reventure install folder (contains Reventure.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? rvDir ?? "")
                                   ? (overrideDir ?? rvDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                if (!LooksLikeReventureDir(picked))
                {
                    // Be forgiving: try the named sub-folder
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeReventureDir(nested))
                        picked = nested;
                    else
                    {
                        MessageBox.Show(
                            "That does not look like a Reventure installation. Pick the " +
                            "folder that contains Reventure.exe (for Steam this is usually " +
                            @"...\steamapps\common\Reventure).",
                            "Not a Reventure folder",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
                SaveOverrideDir(picked);
                dirBox.Text = picked;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(
            dirBtn, System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Steam installs are detected automatically (AppID 900270). Use this " +
                   "picker for a non-standard Steam library location.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection (entered in-game) ─────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in-game)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Start Reventure with the mod installed. The ReventureEndingRando mod " +
                   "provides an in-game Archipelago connection menu. Enter your Server, " +
                   "Slot name, and Password (if any) there. This launcher does not " +
                   "pre-fill the connection — it is entered in-game.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
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
            "1. Own Reventure (Steam AppID 900270). Install it if you have not. Use the " +
                "picker above if it was not auto-detected.",
            "2. Install the mod: use Install on the Play tab to download and extract " +
                "ReventureEndingRando into your Reventure folder, or do it by hand from " +
                "the mod releases (link below).",
            "3. Generate an Archipelago multiworld game that includes Reventure and get " +
                "your connection details (server / slot / password).",
            "4. Press Play. Start Reventure, open the in-game Archipelago connection " +
                "menu, and enter your server details there.",
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
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("ReventureEndingRando (GitHub) ↗", ModRepoUrl),
            ("Reventure Setup Guide ↗",         SetupGuideUrl),
            ("Reventure Info (AP) ↗",           GameInfoUrl),
            ("Archipelago Official ↗",          ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
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
            btn.Click += (_, _) => { try { Process.Start(
                new ProcessStartInfo(u) { UseShellExecute = true }); } catch { } };
            panel.Children.Add(btn);
        }
        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // Mod releases are the AP-relevant news for this game.
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

    /// "v1.2.3" → "1.2.3" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + the Windows zip download URL.
    /// Uses /releases/latest (standard non-prerelease releases). On API failure
    /// returns the version as "unknown" and a null URL so the caller can report
    /// the error clearly.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestModAsync(
        CancellationToken ct)
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
                string? zip = PickWindowsZip(assets);
                if (zip != null)
                    return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited */ }

        // Could not resolve — return a sentinel. The caller will surface the error.
        return ("unknown", null);
    }

    /// From a release's assets array, pick the best Windows .zip:
    ///   prefer any zip that contains "win" in the name; exclude Linux/Mac/source;
    ///   fall back to any non-excluded zip. Returns null when nothing plausible is
    ///   found.
    private static string? PickWindowsZip(JsonElement assets)
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

            if (lower.Contains("linux") || lower.Contains("ubuntu") ||
                lower.Contains("mac")   || lower.Contains("osx")    ||
                lower.Contains("darwin")|| lower.Contains("source"))
                continue;

            anyZip ??= url;
            if (preferred == null &&
                (lower.Contains("win") || lower.Contains("x64") || lower.Contains("x86")))
                preferred = url;
        }

        return preferred ?? anyZip;
    }

    // ── Private helpers — Steam / Reventure detection ─────────────────────────

    /// The Reventure install dir to use: the override (if set and valid) wins,
    /// else the Steam-detected install. Null when nothing is found.
    private string? ResolveReventureDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeReventureDir(ov))
            return ov;

        try { return DetectSteamReventureDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Reventure if it contains Reventure.exe (case-insensitive).
    private static bool LooksLikeReventureDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            // Case-insensitive check on Windows — covers any capitalization.
            foreach (string f in Directory.EnumerateFiles(dir, "*.exe"))
            {
                if (Path.GetFileName(f).Equals(BaseExeName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam Reventure install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_900270.acf exists → steamapps\common\Reventure.
    private static string? DetectSteamReventureDir()
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
                        $"appmanifest_{ReventureSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    // Prefer the installdir named in the manifest; fall back to the
                    // conventional "Reventure" folder name.
                    string  common     = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeReventureDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeReventureDir(conventional)) return conventional;
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    /// Candidate Steam install roots from the registry (HKCU SteamPath +
    /// HKLM WOW6432Node InstallPath + conventional Program Files fallback).
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

    /// Pull every "path" "<value>" pair out of a libraryfolders.vdf body.
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

    /// Find the mod's marker file in the detected/override Reventure install.
    /// Looks for any .dll whose name contains "reventure" (case-insensitive) or
    /// any directory named "BepInEx" (common Unity mod host), and any file named
    /// "ReventureEndingRando" in any extension. Returns the path or null.
    private string? FindInstalledModFile()
    {
        try
        {
            string? rvDir = ResolveReventureDir();
            if (rvDir == null) return null;

            // Fast path: BepInEx directory is a strong signal for Unity mods.
            string bepInExDir = Path.Combine(rvDir, "BepInEx");
            if (Directory.Exists(bepInExDir))
            {
                foreach (string dll in Directory.EnumerateFiles(
                    bepInExDir, "*.dll", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileNameWithoutExtension(dll).ToLowerInvariant();
                    if (name.Contains("reventure") || name.Contains("endingrand"))
                        return dll;
                }
            }

            // Broader scan for any mod DLL at the install root level.
            foreach (string dll in Directory.EnumerateFiles(rvDir, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(dll).ToLowerInvariant();
                if (name.Contains("reventure") && !name.Equals("reventure", StringComparison.Ordinal))
                    return dll;
                if (name.Contains("endingrand") || name.Contains("archipelago"))
                    return dll;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — process launch ─────────────────────────────────────

    private void StartReventure()
    {
        string? rvDir  = ResolveReventureDir();
        string? exeDir = rvDir;

        // Find Reventure.exe (case-insensitive scan).
        string? exePath = null;
        if (rvDir != null)
        {
            try
            {
                foreach (string f in Directory.EnumerateFiles(rvDir, "*.exe"))
                {
                    if (Path.GetFileName(f).Equals(BaseExeName, StringComparison.OrdinalIgnoreCase))
                    {
                        exePath = f;
                        break;
                    }
                }
            }
            catch { }
        }

        if (exePath != null)
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exePath,
                WorkingDirectory = exeDir ?? Path.GetDirectoryName(exePath) ?? "",
                UseShellExecute  = false,
            }) ?? throw new InvalidOperationException(
                "Failed to start Reventure. Check the install folder in Settings.");

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
        else
        {
            // Fall back to Steam launch.
            Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
        }
    }

    // ── Private helpers — download/extract ────────────────────────────────────

    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string targetDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"reventure-mod-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((10, $"Downloading ReventureEndingRando {version}..."));
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
                        int pct = (int)(10 + 60 * downloaded / total);
                        progress.Report((pct,
                            $"Downloading... {downloaded / 1024}KB" +
                            (total > 0 ? $" / {total / 1024}KB" : "")));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((72, "Extracting mod files..."));
            Directory.CreateDirectory(targetDir);

            // Extract directly into the target directory. If the zip wraps
            // everything in a single top-level sub-folder, flatten it so the mod
            // lands directly in the Reventure install directory.
            System.IO.Compression.ZipFile.ExtractToDirectory(
                tempZip, targetDir, overwriteFiles: true);

            // Flatten single-subdir wrapper (common in many mod release zips).
            if (Directory.GetFiles(targetDir).Length == 0)
            {
                string[] subdirs = Directory.GetDirectories(targetDir);
                if (subdirs.Length == 1)
                {
                    string sub = subdirs[0];
                    // Only flatten if the sub-folder looks like a wrapper (not
                    // e.g. BepInEx, which should stay as-is).
                    string subName = Path.GetFileName(sub).ToLowerInvariant();
                    if (!subName.Equals("bepinex") && !subName.Equals("plugins"))
                    {
                        foreach (string fileSrc in Directory.EnumerateFiles(
                            sub, "*", SearchOption.AllDirectories))
                        {
                            string rel     = Path.GetRelativePath(sub, fileSrc);
                            string fileDst = Path.Combine(targetDir, rel);
                            Directory.CreateDirectory(Path.GetDirectoryName(fileDst)!);
                            File.Move(fileSrc, fileDst, overwrite: true);
                        }
                        Directory.Delete(sub, recursive: true);
                    }
                }
            }

            progress.Report((90, "Mod files extracted."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    // ── Private helpers — settings sidecar ───────────────────────────────────
    // Kept in this plugin's OWN JSON file so it stays a single self-contained
    // source file and does not modify Core/SettingsStore.

    private sealed class ReventureSettings
    {
        public string? OverrideDir { get; set; }
    }

    private ReventureSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<ReventureSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(ReventureSettings s)
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

    private string? LoadOverrideDir() => LoadSettings().OverrideDir;

    private void SaveOverrideDir(string dir)
    {
        var s = LoadSettings();
        s.OverrideDir = dir;
        SaveSettings(s);
    }

    // ── Private helpers — version stamp ───────────────────────────────────────

    private string? ReadStampedVersion()
    {
        try
        {
            if (File.Exists(VersionStampPath))
                return File.ReadAllText(VersionStampPath).Trim();
        }
        catch { }
        return null;
    }

    private void WriteStampedVersion(string version)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(VersionStampPath, version, new UTF8Encoding(false));
        }
        catch { /* non-fatal */ }
    }
}
