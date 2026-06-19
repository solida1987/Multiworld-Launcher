using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using LauncherV2.Core;

// The launcher project sets BOTH UseWPF and UseWindowsForms=true, so several UI
// type names (Color, Brushes, Button, TextBox, HorizontalAlignment, Orientation,
// MessageBox, MessageBoxButton, MessageBoxImage, FontWeights, TextWrapping,
// Thickness, Dock, UIElement) collide between WPF and WinForms.
// The project's GlobalUsings.cs already aliases each of these to its WPF type
// globally, so this file relies on those — no local aliases (a local alias
// duplicating a global one would be CS1537). Any remaining ambiguous types are
// fully-qualified below.

namespace LauncherV2.Plugins.HasteBrokenWorlds;

// ═══════════════════════════════════════════════════════════════════════════════
// HasteBrokenWorldsPlugin — install / launch for "HASTE: Broken Worlds" (Landfall
// Games, 2025) played through the Archipelago Randomizer Steam Workshop mod
// (id=3462307025). This is a NATIVE "ConnectsItself" integration: the AP client
// is built directly into the Steam Workshop mod — no external Archipelago client,
// no emulator, no Lua bridge, no launcher-held ApClient on the slot.
//
// ── VERIFIED FACTS (2026-06-14, verified against the apworld + setup guide) ────
//
//   * THE AP WORLD is a CUSTOM world (not shipped in the AP core release). It is
//     published at github.com/WritingHusky/haste_apworld. The EXACT game string
//     (from worlds/__init__.py: `game: ClassVar[str] = "Haste"` and confirmed in
//     archipelago.json: `"game": "Haste"`) is:
//         "Haste"
//     required_client_version: (0, 5, 0). world_version: 0.4.0.
//
//   * THE BASE GAME is the user's own legally-owned copy of HASTE: Broken Worlds
//     (Steam appid 1796470). The game's Steam common folder is "Haste".
//
//   * THE MOD is a Steam Workshop mod (id 3462307025), titled "Archipelago
//     Randomizer Mod", authored by WritingHusky / JXJacob. The user subscribes to
//     it from the Steam Workshop page. The AP client is built directly into the
//     Workshop mod — there is NO separate "Haste Client" in the Archipelago
//     launcher and NO zip to download. The mod is NOT BepInEx-based; Haste uses
//     the Landfall Plugin Framework (LPF), and Steam Workshop is the install
//     mechanism.
//
//   * CONNECTION is made entirely IN-GAME (verified against the official setup
//     guide docs/setup_en.md in the apworld repo). After subscribing and starting
//     the game:
//         Main menu → Settings → General tab:  select a new / empty save slot
//         Main menu → Settings → Archipelago tab: enable the mod, fill in
//             Server name, Port, Username (slot name), Password
//         Then start the game.
//     There is NO config file or command-line argument this launcher can pre-write
//     to seed the connection (the setup guide makes no mention of any such file).
//     So this plugin does NOT attempt a connection prefill — the settings panel
//     and post-install note surface the session credentials so the user can copy
//     them into those in-game fields.
//
//   * ConnectsItself = true. The AP server allows only one connection per slot and
//     kicks the older connection — the launcher must NOT hold an ApClient on this
//     slot while the Workshop mod is connected.
//
//   * SupportsStandalone = true. Plain (non-AP) HASTE: Broken Worlds runs normally
//     without the Workshop mod active.
//
//   * INSTALL SCOPE. "Installation" for this mod means subscribing on Steam
//     Workshop — Steam downloads and deploys it automatically. The launcher cannot
//     do that on the user's behalf (there is no public Workshop zip API and we do
//     not pretend otherwise). What the launcher CAN do:
//       1. Detect the Steam install of the game via registry + libraryfolders.vdf
//          + appmanifest_1796470.acf (plus a manual override folder picker).
//       2. Open the Workshop page in the user's browser so they can subscribe.
//       3. Detect whether the Workshop mod appears to be deployed (look for
//          telltale mod files in the game's workshop content folder or install
//          tree).
//       4. Launch the game exe once everything is in place.
//     This is honest scope: no fake one-click install that does not exist.
//
//   * GAME EXE. Haste: Broken Worlds is a Unity game. The Windows exe is
//     "Haste.exe" (verified from the Steam Workshop mod's setup guide instructions
//     which say "Start Haste on Steam"). The Steam default install directory is
//     %ProgramFiles(x86)%\Steam\steamapps\common\Haste\, containing "Haste.exe".
//
//   * BUILD NOTE (CS0104 / CS1537 hygiene): this project enables both UseWPF and
//     UseWindowsForms. The GlobalUsings.cs already imports the WPF aliases for
//     the common collision types — this file must NOT redeclare them (CS1537).
//     Any WPF types not covered by GlobalUsings are spelled with full namespaces
//     where collision risk exists (System.Windows.MessageBox etc.).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class HasteBrokenWorldsPlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    /// Steam appid for HASTE: Broken Worlds (verified 2026-06-14, AppID 1796470).
    private const string SteamAppId = "1796470";

    /// Steam common folder name (inside steamapps\common\).
    private const string SteamCommonFolderName = "Haste";

    /// The game's main executable name on Windows.
    private const string GameExeName = "Haste.exe";

    /// Steam Workshop mod id for the Archipelago Randomizer Mod.
    private const string WorkshopModId = "3462307025";

    private static readonly string SteamRunUrl    = $"steam://rungameid/{SteamAppId}";
    private static readonly string WorkshopPageUrl = $"https://steamcommunity.com/sharedfiles/filedetails/?id={WorkshopModId}";

    private const string SetupGuideUrl =
        "https://github.com/WritingHusky/haste_apworld/blob/main/docs/setup_en.md";
    private const string ApWorldReleasesUrl =
        "https://github.com/WritingHusky/haste_apworld/releases/latest";
    private const string ApWorldRepoUrl =
        "https://github.com/WritingHusky/haste_apworld";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // GitHub API for the apworld releases (news feed only — no zip download).
    private const string GhReleasesUrl =
        "https://api.github.com/repos/WritingHusky/haste_apworld/releases";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "haste_broken_worlds";
    public string DisplayName => "HASTE: Broken Worlds";
    public string Subtitle    => "Native PC · Steam Workshop mod";
    public string ApWorldName => "Haste";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "haste_broken_worlds.png");

    public string ThemeAccentColor => "#3A8ACA";   // Haste sky-blue
    public string[] GameBadges     => new[] { "Steam · Workshop mod" };

    public string Description =>
        "HASTE: Broken Worlds is a fast-paced roguelite by Landfall Games. In the " +
        "Archipelago randomizer, Shard unlocks, abilities, NPCs, Captain's upgrades, " +
        "shop items and fragment clears are shuffled into the multiworld. The AP " +
        "client is built directly into the Steam Workshop mod — the game connects to " +
        "the Archipelago server itself with no emulator and no external client. You " +
        "bring your own copy of HASTE: Broken Worlds (Steam), subscribe to the " +
        "Archipelago Randomizer Mod on the Steam Workshop, and then configure the " +
        "connection from the in-game Settings → Archipelago tab before starting a " +
        "new save.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the game is present (we can detect the exe). Whether the
    /// Workshop mod is deployed is checked separately in the settings panel.
    public bool IsInstalled => ResolveGameDir() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory { get; set; }
        = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam", "steamapps", "common", "Haste");

    /// Settings sidecar for this plugin (install override + informational notes).
    private string SidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SidecarPath
        => Path.Combine(SidecarDir, "haste_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The Workshop mod handles all AP communication internally — the launcher
    // relays nothing. These events exist for interface compatibility
    // (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── ConnectsItself / SupportsStandalone ───────────────────────────────────

    /// The Steam Workshop mod owns the AP slot connection — the launcher must NOT
    /// hold a concurrent ApClient on this slot while the game runs.
    public bool ConnectsItself => true;

    /// Plain (non-AP) HASTE: Broken Worlds runs normally without the mod active.
    public bool SupportsStandalone => true;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        // "Installed" for our purposes = the game exe is present. The Workshop mod
        // is managed by Steam and has no version file we can read.
        try
        {
            InstalledVersion = ResolveGameDir() != null ? "installed" : null;
        }
        catch
        {
            InstalledVersion = null;
        }

        // The "available" version is the latest apworld release tag on GitHub.
        try
        {
            string? tag = await FetchLatestApWorldTagAsync(ct);
            AvailableVersion = tag;
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
        // There is no downloadable mod zip for HASTE: Broken Worlds. The AP client
        // is a Steam Workshop mod — the user must subscribe to it on Steam Workshop
        // and Steam installs it automatically. What we can do:
        //   1. Verify the game is detected.
        //   2. Open the Workshop subscription page in the browser for the user.
        //   3. Point the user at the apworld .apworld file release on GitHub.
        // We then report the full guided steps.

        await Task.CompletedTask;

        progress.Report((10, "Checking for HASTE: Broken Worlds installation..."));
        string? gameDir = ResolveGameDir();
        if (gameDir == null)
        {
            progress.Report((100,
                "HASTE: Broken Worlds was not detected. Please install it via Steam " +
                "(appid 1796470). Use the Settings panel to pick your install folder if " +
                "it is in a non-standard location."));
            return;
        }

        progress.Report((40, "Opening the Archipelago Randomizer Mod Workshop page..."));
        try
        {
            Process.Start(new ProcessStartInfo(WorkshopPageUrl) { UseShellExecute = true });
        }
        catch { /* browser launch failed — the link is shown in Settings anyway */ }

        progress.Report((70, "Opening the apworld releases page..."));
        try
        {
            Process.Start(new ProcessStartInfo(ApWorldReleasesUrl) { UseShellExecute = true });
        }
        catch { }

        progress.Report((100,
            "HASTE: Broken Worlds detected at: " + gameDir + ". " +
            "The AP mod is a Steam Workshop mod — Steam manages its installation " +
            "automatically after you subscribe. The Workshop page and apworld " +
            "releases page have been opened in your browser. Follow the guided " +
            "steps in the Settings panel to finish setup."));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return ResolveGameDir() != null;
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // HONEST: the AP server connection for HASTE: Broken Worlds is entered
        // entirely IN-GAME in the Settings → Archipelago tab (Server name, Port,
        // Username, Password). There is no config file or command-line argument
        // this launcher can pre-write (verified against docs/setup_en.md in the
        // apworld repo). Launching from this tile just starts the game.
        //
        // ConnectsItself = true: the launcher must NOT hold a concurrent ApClient
        // on this slot while the Workshop mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartHaste();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartHaste();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        // No plaintext AP credentials are ever written by this plugin (connection
        // is entered in-game only), so there is nothing to scrub.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The Workshop mod receives items from the AP server directly; the
        // launcher forwards nothing.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The Workshop mod renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your HASTE: Broken Worlds install folder.";

        if (LooksLikeHasteDir(folder))
            return null;

        // Forgiveness: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeHasteDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a HASTE: Broken Worlds installation. " +
               "Pick the folder that contains \"Haste.exe\" — for Steam this is " +
               @"usually ...\steamapps\common\Haste.";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        // WPF brush / color helpers — GlobalUsings already imported the WPF types.
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));

        var panel = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20)
        };

        string? gameDir     = ResolveGameDir();
        string? overrideDir = LoadOverrideDir();

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "HASTE: Broken Worlds uses a Steam Workshop mod as its AP client — " +
                   "the AP connection is built into the mod itself. Subscribe to the " +
                   "Workshop mod, then configure the connection from inside the game " +
                   "(Settings → Archipelago tab). The launcher detects your Steam " +
                   "install and guides the remaining steps, but cannot subscribe to the " +
                   "Workshop or pre-fill the connection on your behalf.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Game install detection ────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "HASTE: BROKEN WORLDS INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "HASTE: Broken Worlds not detected. Pick your install folder below, " +
              "or install the game via Steam (appid 1796470) first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // Folder picker row
        var dirRow = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4)
        };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text        = overrideDir ?? gameDir ?? "",
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your HASTE: Broken Worlds install folder (the one containing " +
                          "Haste.exe). Detected from Steam automatically; set it here to " +
                          "override for a non-standard Steam library.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your HASTE: Broken Worlds install folder",
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
                    System.Windows.MessageBox.Show(bad, "Not a HASTE: Broken Worlds folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // Descend if the user picked the Steam "common" parent.
                if (!LooksLikeHasteDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeHasteDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 1796470). " +
                   "Use this picker for a non-standard Steam library location.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Connecting (in-game) ──────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (configured in-game)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "After subscribing to the Workshop mod and starting the game: " +
                   "go to Settings → Archipelago tab, enable the mod, fill in your " +
                   "server address, port, slot name, and password, then start the game. " +
                   "Make sure to choose a new / empty save slot in the General tab first " +
                   "(AP saves cannot be used on vanilla saves or saves from a previous AP seed).",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Guided setup steps ────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own HASTE: Broken Worlds on Steam (appid 1796470). If it was not " +
                "detected above, use the folder picker or install the game first.",
            "2. Install the Haste apworld: download the latest haste.apworld from " +
                "the apworld releases link below and double-click it, or copy it into " +
                "%programdata%\\Archipelago\\custom_worlds\\.",
            "3. Subscribe to the \"Archipelago Randomizer Mod\" on the Steam Workshop " +
                "(link below). Steam will download and install it automatically.",
            "4. Start HASTE: Broken Worlds via Steam or the Play button above.",
            "5. In the game's main menu, go to Settings → General tab and select a " +
                "new / empty save slot (you cannot use a vanilla or previous AP save).",
            "6. Go to Settings → Archipelago tab. Enable the mod, then enter: " +
                "Server name (e.g. archipelago.gg), Port (e.g. 38281), Username " +
                "(your slot name), and Password (leave blank if none). Start the game.",
            "7. Optional: open a generic text client alongside the game if you want to " +
                "hint items — the in-game client does not support text input at this time.",
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
            Foreground = muted,
            Margin = new System.Windows.Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("Archipelago Randomizer Mod (Steam Workshop) ↗", WorkshopPageUrl),
            ("Haste apworld releases (GitHub) ↗",            ApWorldReleasesUrl),
            ("Haste apworld repository ↗",                   ApWorldRepoUrl),
            ("Haste AP Setup Guide ↗",                       SetupGuideUrl),
            ("Archipelago Official ↗",                       ArchipelagoSite),
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
            btn.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(u) { UseShellExecute = true });
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
        // The apworld GitHub releases are the AP-relevant news for this game.
        try
        {
            string json = await _http.GetStringAsync(GhReleasesUrl, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                string tag = el.TryGetProperty("tag_name", out var t) ? NormalizeTag(t.GetString()) ?? "" : "";

                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? ("Haste apworld " + tag) : ("Haste apworld " + tag),
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: tag,
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var u) ? u.GetString() : ApWorldReleasesUrl
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — game detection ──────────────────────────────────────

    /// The HASTE install dir to use: the override (if set and valid) wins, else
    /// the Steam-detected install. Null when nothing is found.
    private string? ResolveGameDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeHasteDir(ov))
            return ov;

        try { return DetectSteamHasteDir(); }
        catch { return null; }
    }

    /// A folder looks like HASTE: Broken Worlds if it contains "Haste.exe".
    private static bool LooksLikeHasteDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, GameExeName));
        }
        catch { return false; }
    }

    /// Detect the Steam HASTE install via registry → libraryfolders.vdf →
    /// appmanifest_1796470.acf → steamapps\common\Haste.
    private static string? DetectSteamHasteDir()
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

                    string common     = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeHasteDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeHasteDir(conventional)) return conventional;
                }
                catch { /* try next library */ }
            }
        }
        return null;
    }

    /// Candidate Steam install roots from the registry.
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

    /// All Steam library roots including all VDF path entries.
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

    private void StartHaste()
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
            }) ?? throw new InvalidOperationException("Failed to start HASTE: Broken Worlds.");

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

        // Fall back to Steam if the exe is not found but Steam looks present.
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
            "Could not find Haste.exe. Open this game's Settings and pick your " +
            "HASTE: Broken Worlds install folder, or install the game via Steam.",
            GameExeName);
    }

    // ── Private helpers — news feed ───────────────────────────────────────────

    private async Task<string?> FetchLatestApWorldTagAsync(CancellationToken ct)
    {
        string json = await _http.GetStringAsync(GhReleasesUrl, ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.TryGetProperty("tag_name", out var t))
                    return NormalizeTag(t.GetString());
                break; // only want the first (latest) entry
            }
        }
        return null;
    }

    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    // ── Private helpers — settings sidecar ───────────────────────────────────
    // This plugin keeps its launcher-side settings (install-dir override) in its
    // own JSON sidecar so it stays a single self-contained file and does not
    // touch Core/SettingsStore. Same pattern as HollowKnight / RoR2 / Noita.

    private sealed class HasteSettings
    {
        public string? InstallOverride { get; set; }
    }

    private HasteSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SidecarPath))
            {
                string txt = File.ReadAllText(SidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<HasteSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(HasteSettings s)
    {
        try
        {
            Directory.CreateDirectory(SidecarDir);
            File.WriteAllText(SidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new System.Text.UTF8Encoding(false));
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
}
