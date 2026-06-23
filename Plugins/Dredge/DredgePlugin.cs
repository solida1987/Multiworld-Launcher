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

namespace LauncherV2.Plugins.Dredge;

// ═══════════════════════════════════════════════════════════════════════════════
// DredgePlugin — install / launch for "DREDGE" (Black Salt Games / Team17, 2023)
// played through the "Archipelago DREDGE" mod by Alextric, which contains the
// in-game Archipelago Multiworld client. This is a NATIVE "ConnectsItself"
// integration: the mod speaks to the AP server directly with no emulator, no
// Lua bridge, and no launcher-held ApClient on the slot.
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of DREDGE (Steam appid 1562430), and Archipelago support is delivered through
// the DREDGE mod ecosystem on top. The honest integration ceiling is "automate
// what is possible, guide the irreducible parts." The verified facts:
//
//   * THE AP WORLD game string is "DREDGE" (verified against
//     worlds/dredge/world.py: `class DREDGEWorld(World): game = "DREDGE"`
//     and worlds/dredge/archipelago.json: `"game": "DREDGE"`).
//     GameId here = "dredge". The apworld lives at:
//     https://github.com/alextric234/ArchipelagoDredge (custom — not core AP).
//     Releases: dredge.apworld + DREDGE.yaml per release; the host drops
//     dredge.apworld into their AP server's custom_worlds folder.
//
//   * THE MOD FRAMEWORK is Winch — DREDGE uses its own mod loader "Winch" by
//     Hacktix (https://github.com/DREDGE-Mods/Winch), NOT BepInEx. The entire
//     modding ecosystem is managed through "DREDGE Mod Manager" (dredgemods.com).
//     The mod is listed in DREDGE Mod Manager as "Archipelago DREDGE"; Winch
//     is a prerequisite also installed via the manager.
//
//   * CRITICAL HONESTY — THE DREDGE MOD IS INSTALLED THROUGH DREDGE MOD MANAGER
//     (dredgemods.com), NOT via a plain downloadable zip. The Archipelago AP repo
//     for this game (alextric234/ArchipelagoDredge) ships only the .apworld and
//     .yaml — the actual game mod binary is distributed exclusively through the
//     DREDGE mod ecosystem. This plugin therefore CANNOT stage the mod like a
//     BepInEx plugin; the only honest approach is to detect the game install
//     and guide the user through the mod manager setup. The manager itself is a
//     one-click installer (dredgemods.com/manager) and this plugin opens that
//     URL and gives numbered guided steps.
//
//   * CONNECTION is made IN-GAME (verified against the official AP setup guide
//     at worlds/dredge/docs/setup_en.md, 2026-06-14). Three equivalent methods:
//       1. In-game terminal (press ` / ~ key): `ap connect <hostname> <port> <slot> [-p <password>]`
//       2. F7 pop-up UI: enter host / port / slot / password, click Connect.
//       3. Mods menu inside DREDGE (Settings → Mods tab): enter details, press F8.
//     All three share the same saved config — changes in one appear in all. Safe
//     to disconnect and reconnect without restarting DREDGE. Because all methods
//     are in-game and the config file path / format are NOT publicly documented
//     for this mod, this plugin does NOT pre-write any config file. The settings
//     panel surfaces the session credentials for the user to enter in-game.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam DREDGE install via the Windows registry (HKCU SteamPath,
//      HKLM WOW6432Node InstallPath), parsing steamapps\libraryfolders.vdf for
//      every library root and finding appmanifest_1562430.acf → steamapps\common\DREDGE.
//      A manual install-dir OVERRIDE (folder picker) is also supported and takes
//      precedence; it is validated (must contain DREDGE.exe) and persisted in this
//      plugin's OWN sidecar (Games/ROMs/dredge/dredge_launcher.json).
//   2. INSTALL/UPDATE guidance = open dredgemods.com/manager (or the apworld
//      releases page) + give numbered guided steps. Mark "installed" when the Winch
//      mod subfolder or Archipelago DREDGE DLL is found inside the detected DREDGE
//      install tree — honouring a hand-installed setup the same as a managed one.
//   3. LAUNCH = run DREDGE.exe from the detected/override install; if the exe is
//      not found but Steam is present, fall back to steam://rungameid/1562430.
//      ConnectsItself = true (the mod owns the slot). SupportsStandalone = true
//      (DREDGE runs fine without the AP mod active).
//
// ── DEFENSIVE / UNVERIFIED ────────────────────────────────────────────────────
//   * "Installed" is judged by the presence of a Winch install OR an "Archipelago"
//     mod DLL under the detected install tree (case-insensitive, recursive) — NOT
//     by our own version stamp, because the user may install via DREDGE Mod Manager.
//   * Steam library parsing is defensive: tolerant VDF scan; any failure degrades
//     to "DREDGE not found" rather than throwing.
//   * No plaintext AP password is ever written by this plugin.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class DredgePlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const string MOD_OWNER = "alextric234";
    private const string MOD_REPO  = "ArchipelagoDredge";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";
    private const string GH_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";

    private const string DredgeModManagerUrl = "https://dredgemods.com/manager/";
    private const string SetupGuideUrl       = "https://archipelago.gg/tutorial/DREDGE/setup/en";
    private const string GameInfoUrl         = "https://archipelago.gg/games/DREDGE/info/en";
    private const string ArchipelagoSite     = "https://archipelago.gg";

    // Steam — DREDGE appid 1562430 (Black Salt Games / Team17, 2023).
    private const string DredgeSteamAppId   = "1562430";
    private static readonly string SteamRunUrl = $"steam://rungameid/{DredgeSteamAppId}";

    /// Standard Steam install sub-folder name for DREDGE.
    private const string SteamCommonFolderName = "DREDGE";

    /// The base-game executable name.
    private const string DredgeExeName = "DREDGE.exe";

    /// Winch mod loader subfolder (present when Winch is installed into DREDGE).
    private const string WinchFolderName = "Winch";

    /// The Archipelago mod DLL name inside the Winch mods tree.
    private const string ArchipelagoModDllName = "Archipelago.dll";

    /// Pinned fallback apworld version when GitHub is unreachable.
    private const string FallbackApWorldVersion = "0.5.1";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "dredge";
    public string DisplayName => "DREDGE";
    public string Subtitle    => "Native PC · Archipelago mod (Winch)";

    /// EXACT AP game string — verified against worlds/dredge/world.py and
    /// worlds/dredge/archipelago.json: `"game": "DREDGE"`.
    public string ApWorldName => "DREDGE";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "dredge.png");

    /// Deep ocean teal — DREDGE's signature dark nautical palette.
    public string ThemeAccentColor => "#2A7A8E";

    public string[] GameBadges => new[] { "Steam · needs mod", "Winch mod loader" };

    public string Description =>
        "DREDGE, the 2023 dark fishing adventure by Black Salt Games (Team17), " +
        "played through the Archipelago DREDGE mod by Alextric — which bundles an " +
        "in-game Archipelago client so the game connects to the multiworld itself " +
        "with no emulator and no bridge. Fish species, research upgrades, boat " +
        "equipment, hull upgrades, and the five relics are shuffled across the " +
        "multiworld. Goal: collect all five relics and deliver them to The Collector. " +
        "You bring your own copy of DREDGE (owned on Steam); the mod runs on the " +
        "Winch mod loader via DREDGE Mod Manager. The launcher detects your Steam " +
        "install and guides you through the two-step Winch + mod setup. You connect " +
        "to your server from inside the game using the terminal, F7 popup, or the " +
        "Mods menu.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" = the Winch loader OR the Archipelago mod DLL is found in
    /// the detected/override DREDGE install tree. Honors a hand-installed setup.
    public bool IsInstalled => FindArchipelagoModDll() != null || WinchPresent(ResolveDredgeDir());

    public bool IsRunning { get; private set; }

    // ── Architecture ──────────────────────────────────────────────────────────

    /// The DREDGE mod connects directly to the AP server — the launcher must NOT
    /// hold its own ApClient on this slot while the mod is active.
    public bool ConnectsItself => true;

    /// Plain DREDGE runs perfectly well without the AP mod.
    public bool SupportsStandalone => true;

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Dredge");

    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "dredge_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────

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
            bool modFound = FindArchipelagoModDll() != null || WinchPresent(ResolveDredgeDir());
            InstalledVersion = modFound
                ? (ReadStampedVersion() ?? "installed")
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

    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // HONEST: the DREDGE Archipelago mod is distributed through DREDGE Mod
        // Manager (dredgemods.com) — not as a plain downloadable zip that this
        // launcher can stage. The correct install path is:
        //   1. Install DREDGE Mod Manager from dredgemods.com/manager
        //   2. In the manager search for and install "Winch"
        //   3. In the manager search for and install "Archipelago DREDGE"
        //   4. Press Play in the manager
        // This plugin opens the mod manager page and gives the user clear guided
        // steps rather than faking a one-click install that doesn't exist.
        progress.Report((10, "Opening DREDGE Mod Manager page in your browser..."));
        try
        {
            Process.Start(new ProcessStartInfo(DredgeModManagerUrl) { UseShellExecute = true });
        }
        catch { /* non-fatal — URL printed in the settings panel */ }

        progress.Report((100,
            "DREDGE Mod Manager page opened. Install steps: " +
            "(1) Download and run DREDGE Mod Manager from dredgemods.com/manager. " +
            "(2) In the manager, search for and install \"Winch\" (the mod loader). " +
            "(3) In the manager, search for and install \"Archipelago DREDGE\". " +
            "(4) Press Play in DREDGE Mod Manager to launch the game with the mod. " +
            "You only need to use the manager for installs/updates — after that, you can " +
            "launch DREDGE.exe directly. See Settings for the full guided steps and links."));

        return Task.CompletedTask;
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
        // HONEST: connection is entered in-game via the terminal, F7 popup, or the
        // Mods menu — there is no documented CLI arg or config pre-write for this
        // mod. Launching just starts the game; the user enters their session
        // credentials in-game. ConnectsItself = true: the launcher must NOT hold
        // its own ApClient on this slot.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartDredge();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartDredge();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning        = false;
        _gameProcess     = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Honest mod-manager notice ─────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "DREDGE uses its own mod loader (Winch), installed via DREDGE Mod " +
                   "Manager (dredgemods.com). The launcher can detect your DREDGE install " +
                   "and launch the game, but the AP mod must be installed through the mod " +
                   "manager — see the guided steps below. After installing, you connect to " +
                   "your server from inside the game. These external steps are not verified " +
                   "by this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "DREDGE INSTALL", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? dredgeDir   = ResolveDredgeDir();
        string? overrideDir = LoadOverrideDir();
        string detectMsg = dredgeDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + dredgeDir
                : "Detected Steam install: " + dredgeDir)
            : "DREDGE not detected. Pick your install folder below, or install " +
              "DREDGE via Steam first.";

        panel.Children.Add(new TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = dredgeDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // Winch status
        bool winchOk = dredgeDir != null && WinchPresent(dredgeDir);
        panel.Children.Add(new TextBlock
        {
            Text = dredgeDir == null ? "" :
                   winchOk
                       ? "Winch mod loader found in your DREDGE folder."
                       : "Winch not found yet — install it via DREDGE Mod Manager (see steps below).",
            FontSize = 11, Foreground = winchOk ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });

        // Archipelago mod status
        string? modDll = FindArchipelagoModDll();
        panel.Children.Add(new TextBlock
        {
            Text = modDll != null
                       ? "Archipelago DREDGE mod found: " + modDll
                       : "Archipelago DREDGE mod not found yet — install it via DREDGE " +
                         "Mod Manager (see steps below).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        // Folder picker row
        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? dredgeDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip = "Your DREDGE install folder (the one containing DREDGE.exe). " +
                      "Detected from Steam automatically; set it here to override " +
                      "(non-standard Steam library or another store).",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120, Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select your DREDGE install folder (contains DREDGE.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? dredgeDir ?? "")
                                   ? (overrideDir ?? dredgeDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a DREDGE folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                if (!LooksLikeDredgeDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeDredgeDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 1562430). Use " +
                   "this picker for a non-standard Steam library or another store.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection ───────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "CONNECTING (entered in-game)", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Three equivalent methods — all share the same saved config:\n" +
                   " • Terminal (press ` or ~ key): type  ap connect <hostname> <port> <slot> [-p <password>]\n" +
                   " • F7 pop-up UI: enter host / port / slot / password, click Connect.\n" +
                   " • DREDGE Mods menu (Settings → Mods): enter details, then press F8 to connect (F10 to disconnect).\n" +
                   "You can safely disconnect and reconnect at any time without restarting DREDGE. " +
                   "This launcher does not pre-fill the connection.",
            FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own DREDGE (on Steam). Install it if you have not. Use the picker above " +
                "if it was not detected automatically.",
            "2. Download and run DREDGE Mod Manager from dredgemods.com/manager — click the " +
                "\"Install\" button on the Play tab to open the download page.",
            "3. In DREDGE Mod Manager, search for \"Winch\" and install it (the mod loader " +
                "DREDGE uses for all mods).",
            "4. In DREDGE Mod Manager, search for \"Archipelago DREDGE\" and install it.",
            "5. Press Play in DREDGE Mod Manager to confirm the mods load correctly. " +
                "A \"DREDGE Mods\" entry should appear in the in-game Settings menu.",
            "6. To play: load a save, then connect to your Archipelago server using the " +
                "terminal (` key), F7 popup, or the Mods menu. The game must be set to " +
                "English for checks to send correctly.",
        })
        {
            panel.Children.Add(new TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5),
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
            ("DREDGE Mod Manager ↗",              DredgeModManagerUrl),
            ("Archipelago DREDGE (GitHub) ↗",     ModRepoUrl),
            ("DREDGE Setup Guide ↗",              SetupGuideUrl),
            ("DREDGE Guide (AP) ↗",               GameInfoUrl),
            ("Archipelago Official ↗",            ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label, HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0), FontSize = 12,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
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
            string json = await _http.GetStringAsync(GH_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var result = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                result.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : "",
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: el.TryGetProperty("tag_name", out var t) ? NormalizeTag(t.GetString()) ?? "" : "",
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var u) ? u.GetString() : null
                ));
                if (result.Count >= 10) break;
            }
            return result.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Public helpers (for Settings folder picker validation) ────────────────

    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your DREDGE install folder.";

        if (LooksLikeDredgeDir(folder))
            return null;

        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeDredgeDir(nested))
                return null;
        }
        catch { }

        return "That does not look like a DREDGE installation. Pick the folder that " +
               "contains DREDGE.exe (for Steam this is usually " +
               @"...\steamapps\common\DREDGE).";
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        // Tags are like "dredge_0.5.1" → "0.5.1"
        if (tag.StartsWith("dredge_", StringComparison.OrdinalIgnoreCase))
            tag = tag["dredge_".Length..];
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    private async Task<string?> ResolveLatestApWorldVersionAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    // ── Private helpers — Steam / DREDGE detection ────────────────────────────

    private string? ResolveDredgeDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeDredgeDir(ov))
            return ov;

        try { return DetectSteamDredgeDir(); }
        catch { return null; }
    }

    private static bool LooksLikeDredgeDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, DredgeExeName))) return true;
            if (Directory.Exists(Path.Combine(dir, "DREDGE_Data"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// True when Winch appears installed in a DREDGE folder. Winch installs a
    /// "Winch" subdirectory at the DREDGE root, plus winch_doorstop.dll or similar.
    private static bool WinchPresent(string? dredgeDir)
    {
        if (string.IsNullOrWhiteSpace(dredgeDir) || !Directory.Exists(dredgeDir))
            return false;
        try
        {
            if (Directory.Exists(Path.Combine(dredgeDir, WinchFolderName))) return true;
            // Winch also places a doorstop proxy dll at the root.
            foreach (string dll in Directory.EnumerateFiles(dredgeDir, "*.dll", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(dll);
                if (name.StartsWith("winch", StringComparison.OrdinalIgnoreCase)) return true;
                if (name.Equals("doorstop.dll", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { /* permission / vanished */ }
        return false;
    }

    private static string? DetectSteamDredgeDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{DredgeSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common    = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeDredgeDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeDredgeDir(conventional)) return conventional;
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

    // ── Private helpers — installed-mod detection ─────────────────────────────

    /// Find the Archipelago.dll under the detected/override DREDGE install's
    /// Winch mods tree (recursive, case-insensitive). Returns the dll path or null.
    private string? FindArchipelagoModDll()
    {
        try
        {
            string? dredge = ResolveDredgeDir();
            if (dredge == null) return null;

            // Winch mods live under <DREDGE>\Winch\mods\ (one subfolder per mod).
            string winchDir = Path.Combine(dredge, WinchFolderName);
            if (!Directory.Exists(winchDir))
            {
                // Fallback: scan the entire DREDGE tree for any Archipelago.dll
                foreach (string dll in Directory.EnumerateFiles(dredge, "*.dll", SearchOption.AllDirectories))
                {
                    if (Path.GetFileName(dll).Equals(ArchipelagoModDllName, StringComparison.OrdinalIgnoreCase))
                        return dll;
                }
                return null;
            }

            foreach (string dll in Directory.EnumerateFiles(winchDir, "*.dll", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(dll).Equals(ArchipelagoModDllName, StringComparison.OrdinalIgnoreCase))
                    return dll;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartDredge()
    {
        string? dredge = ResolveDredgeDir();
        string? exe    = dredge != null ? Path.Combine(dredge, DredgeExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = dredge!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start DREDGE.");

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

        // Fall back to Steam.
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
            "Could not find DREDGE.exe. Open this game's Settings and pick your DREDGE " +
            "install folder, or install DREDGE via Steam.",
            DredgeExeName);
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class DredgeSettings
    {
        public string? InstallOverride { get; set; }
        public string? ApWorldVersion  { get; set; }
    }

    private DredgeSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<DredgeSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(DredgeSettings s)
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

    private void SaveOverrideDir(string p)
    {
        var s = LoadSettings(); s.InstallOverride = p; SaveSettings(s);
    }

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().ApWorldVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
}
