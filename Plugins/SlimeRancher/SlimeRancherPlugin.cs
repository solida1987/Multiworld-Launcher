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

// NOTE: WPF UI types are FULLY QUALIFIED throughout this file (System.Windows.*).
// The real launcher project sets <UseWindowsForms>true</UseWindowsForms> alongside
// WPF, so the bare names Button / TextBox / Color / Brushes / MessageBox /
// FontWeights / Orientation / Clipboard collide between System.Windows.Forms and
// System.Windows[.Controls/.Media] (CS0104). Qualifying every UI type avoids that.
// No file-level `using X = System.Windows...;` aliases (they duplicate GlobalUsings
// → CS1537).

namespace LauncherV2.Plugins.SlimeRancher;

// ═══════════════════════════════════════════════════════════════════════════════
// SlimeRancherPlugin — install / launch for "Slime Rancher" (Monomi Park, 2017)
// played through the Slimipelago mod by SWCreeperKing, a BepInEx 5 plugin that is
// the in-game Archipelago client for Slime Rancher. This is a NATIVE
// "ConnectsItself" integration — the game itself speaks to the AP server (no
// emulator, no Lua bridge, no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified online) ────────────────────────
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of Slime Rancher (Steam appid 433340), and Archipelago support is delivered as
// a BepInEx 5 mod (Slimipelago) added on top. The honest integration ceiling —
// exactly like the shipped Subnautica / Noita / RoR2 plugins — is "automate what
// is possible, guide the irreducible parts." The verified facts:
//
//   * THE AP WORLD game string is "Slime Rancher" (mod repo: SWCreeperKing/
//     Slimipelago; the apworld declares game = "Slime Rancher"). GameId = "slime_rancher".
//
//   * THE MOD repo is SWCreeperKing/Slimipelago on GitHub. Releases ship one or
//     more zip assets. The plugin resolves the newest release dynamically via the
//     GitHub API; when offline it falls back to a pinned direct-download URL.
//
//   * THE ZIP INSTALL: a Slimipelago release zip contains BepInEx plugin DLL(s)
//     under a BepInEx/plugins/ tree (or at the zip root). This plugin downloads the
//     zip and extracts it, staging the plugin DLL into
//     <SlimeRancher>\BepInEx\plugins\Slimipelago\. Because BepInEx 5 must already
//     be installed for the plugin to load, this plugin also checks for BepInEx and
//     presents the guided steps so the user can install BepInEx if missing. The
//     BepInEx installer (from github.com/BepInEx/BepInEx) is the user's own step —
//     this launcher surfaces the link clearly.
//
//   * CONNECTION is made IN-GAME (per the mod's design). After the game is launched
//     with Slimipelago active, the mod provides an in-game UI or config to enter
//     the AP server address, slot name, and password. There is NO command-line / no
//     config file this launcher can pre-write to prefill the connection. The settings
//     panel surfaces the session's server/slot so the user can copy them.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Slime Rancher install via Windows registry + VDF parsing.
//      A manual install-dir OVERRIDE (settings folder picker) is also supported;
//      it is validated (must contain SlimeRancher.exe) and persisted in this
//      plugin's own sidecar (Games/ROMs/slime_rancher/slime_rancher_launcher.json).
//   2. INSTALL/UPDATE: download the latest Slimipelago release zip from GitHub,
//      extract the BepInEx plugin into the Steam game's BepInEx\plugins folder.
//      Report whether BepInEx itself is present; surface guided steps and links for
//      BepInEx install if it is not. Never claim a one-click that cannot exist when
//      BepInEx itself is missing.
//   3. LAUNCH = run SlimeRancher.exe from the detected/override install; if the exe
//      cannot be found but Steam is present, fall back to steam://rungameid/433340.
//      ConnectsItself = true. SupportsStandalone = true.
//
// ── BUILD NOTE ─────────────────────────────────────────────────────────────────
//   * UseWindowsForms=true: all WPF UI types are fully qualified (System.Windows.*).
//   * No file-level `using X = System.Windows...;` aliases — they collide with
//     GlobalUsings (CS1537).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SlimeRancherPlugin : IGamePlugin
{
    // ── Constants — Slimipelago mod (GitHub, SWCreeperKing/Slimipelago) ────────
    private const string MOD_OWNER = "SWCreeperKing";
    private const string MOD_REPO  = "Slimipelago";

    private const string ModRepoUrl =
        $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string ModReleasesPageUrl =
        $"{ModRepoUrl}/releases";
    private const string GH_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string BepInExSite =
        "https://github.com/BepInEx/BepInEx/releases";
    private const string BepInEx5GuideUrl =
        "https://docs.bepinex.dev/articles/user_guide/installation/index.html";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Slime Rancher appid 433340.
    private const string SteamAppId = "433340";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    /// The standard Steam install sub-folder name.
    private const string SteamCommonFolderName = "Slime Rancher";

    /// The game's main executable inside the install folder.
    private const string GameExeName = "SlimeRancher.exe";

    /// Pinned fallback tag when the GitHub API is unreachable. The zip URL is
    /// constructed from the tag and the expected asset name; it is best-effort (the
    /// actual filename may differ between releases — the API path is preferred).
    private const string FallbackVersion = "1.0.0";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/{FallbackVersion}/Slimipelago.zip";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "slime_rancher";
    public string DisplayName => "Slime Rancher";
    public string Subtitle    => "Native PC · Slimipelago mod";

    /// EXACT AP world game string (matches apworld: game = "Slime Rancher").
    public string ApWorldName => "Slime Rancher";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "slime_rancher.png");

    /// Pink — cute slime aesthetic (#FF69B4).
    public string ThemeAccentColor => "#FF69B4";

    public string[] GameBadges => new[] { "Steam · BepInEx mod" };

    public string Description =>
        "Slime Rancher, Monomi Park's cozy first-person ranching simulation, played " +
        "through Slimipelago — a BepInEx 5 mod by SWCreeperKing that wires the game " +
        "into Archipelago Multiworld. Ranch activities, plot milestones, and " +
        "exploration checks are shuffled across the multiworld, and the mod connects " +
        "to the Archipelago server from inside the game. You bring your own copy of " +
        "Slime Rancher (Steam), and Slimipelago is installed via BepInEx 5 on top " +
        "of it. The launcher detects your Steam install, downloads and stages the " +
        "Slimipelago plugin, and guides you through the BepInEx setup if needed. " +
        "You connect to your server from within the game using Slimipelago's " +
        "in-game Archipelago UI.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means a Slimipelago plugin DLL is present in the detected/
    /// override game install's BepInEx\plugins tree (case-insensitive, recursive).
    /// We do NOT gate on our own stamp so manual installs are also honoured.
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads and bookkeeping. The actual mod files go
    /// INTO the Slime Rancher install's BepInEx\plugins folder, not here. Exposed
    /// as GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "SlimeRancher");

    /// This plugin's own settings sidecar (install-dir override + version stamp).
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "slime_rancher_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // Slimipelago owns the slot connection (ConnectsItself = true). These events
    // exist for IGamePlugin interface compatibility but are never raised here.
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
        // 0. We need a Slime Rancher install to drop the mod into.
        progress.Report((2, "Locating your Slime Rancher installation..."));
        string? gameDir = ResolveGameDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a Slime Rancher installation. Open this game's " +
                "Settings and pick your Slime Rancher folder (the one containing " +
                "\"SlimeRancher.exe\"), or install Slime Rancher via Steam first. " +
                "The Slimipelago mod is added on top of your own copy of the game.");

        // 1. Resolve the latest mod release (falls back to pinned when offline).
        progress.Report((6, "Checking the latest Slimipelago release on GitHub..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the Slimipelago mod download on GitHub. " +
                "Check your internet connection, or download the mod zip manually " +
                "from " + ModReleasesPageUrl + " and extract it into your Slime " +
                "Rancher BepInEx\\plugins folder. See Settings for the guided steps.");

        // 2. Download + extract the mod zip into the game's BepInEx\plugins dir.
        string pluginsDir = Path.Combine(gameDir, "BepInEx", "plugins");
        string modPluginDir = Path.Combine(pluginsDir, "Slimipelago");
        await DownloadAndExtractModAsync(zipUrl, version, modPluginDir, progress, ct);

        // 3. Stamp the version we installed (informational only — IsInstalled is
        //    judged by the mod DLL's presence, not this stamp).
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool bepInExPresent = Directory.Exists(Path.Combine(gameDir, "BepInEx", "core"));
        progress.Report((100,
            $"Staged Slimipelago {version} into your Slime Rancher BepInEx\\plugins folder. " +
            (bepInExPresent
                ? "BepInEx is present. "
                : "IMPORTANT: BepInEx does not appear to be installed in your Slime " +
                  "Rancher folder. Slimipelago requires BepInEx 5 to load — install " +
                  "it from github.com/BepInEx/BepInEx/releases (choose the x64 build " +
                  "for a 64-bit Unity game), then extract it into your Slime Rancher " +
                  "install folder so that SlimeRancher\\BepInEx is a valid path. " +
                  "See Settings for the guided steps and links. ") +
            "To play: launch the game, then use Slimipelago's in-game Archipelago UI " +
            "to enter your server address, slot name, and password."));
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
        // HONEST: the AP server connection for Slimipelago is entered IN-GAME via
        // the mod's own Archipelago UI. There is no command-line argument / config
        // file this launcher can pre-write to prefill the connection. So launching
        // just starts the game; the user connects in-game.
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while Slimipelago is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartSlimeRancher();
        return Task.CompletedTask;
    }

    /// Plain Slime Rancher without Archipelago runs normally.
    public bool SupportsStandalone => true;

    /// Slimipelago owns the slot connection — the launcher must not hold a parallel
    /// ApClient on the same slot.
    public bool ConnectsItself => true;

    /// Not required by IGamePlugin; unused.
    public bool? BuiltAgainstDataPackageChecksum => null;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartSlimeRancher();
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
    {
        // Slimipelago receives items from the AP server directly inside the game.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid Slime Rancher folder contains
    /// "SlimeRancher.exe". Returns null when valid, else a short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Slime Rancher install folder.";

        if (LooksLikeGameDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeGameDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Slime Rancher installation. Pick the folder " +
               "that contains \"SlimeRancher.exe\". For Steam this is usually " +
               @"...\steamapps\common\Slime Rancher.";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var accent  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0xFF, 0x69, 0xB4));

        var panel = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20)
        };

        string? gameDir     = ResolveGameDir();
        string? overrideDir = LoadOverrideDir();
        string? modDll      = FindInstalledModDll();
        bool    bepInExOk   = gameDir != null &&
                              Directory.Exists(Path.Combine(gameDir, "BepInEx", "core"));

        // ── Header ────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Slime Rancher is your own game (Steam) with the Slimipelago " +
                   "Archipelago mod added on top via BepInEx 5. The launcher can " +
                   "detect your Steam install and stage the Slimipelago plugin, but " +
                   "BepInEx itself must be installed separately into the game folder " +
                   "if it is not already there. You connect to your Archipelago server " +
                   "from within the game using Slimipelago's in-game UI. " +
                   "These external steps are not verified by this launcher.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: game install ──────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "SLIME RANCHER INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Slime Rancher not detected. Pick your install folder below, or " +
              "install Slime Rancher via Steam first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = bepInExOk
                ? "BepInEx found (BepInEx\\core present)."
                : "BepInEx not found. Install BepInEx 5 (x64) into your Slime Rancher " +
                  "folder so that SlimeRancher\\BepInEx\\core exists — see links below.",
            FontSize = 11,
            Foreground = bepInExOk ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                ? "Slimipelago mod found: " + modDll
                : "Slimipelago mod not found in BepInEx\\plugins yet (use Install on " +
                  "the Play tab, or extract the mod zip there manually).",
            FontSize = 11,
            Foreground = modDll != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // ── Folder picker ─────────────────────────────────────────────────
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
            ToolTip     = "Your Slime Rancher install folder (the one containing " +
                          "\"SlimeRancher.exe\"). Detected from Steam automatically; " +
                          "set it here to override (non-standard Steam library, etc.).",
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
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Slime Rancher install folder (contains SlimeRancher.exe)",
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
                    System.Windows.MessageBox.Show(
                        bad, "Not a Slime Rancher folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // Descend into the game folder if the user picked the "common" parent.
                if (!LooksLikeGameDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeGameDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 433340). Use " +
                   "this picker for non-standard Steam libraries or manual installs.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: connection ────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in-game via Slimipelago's Archipelago UI)",
            FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "After launching Slime Rancher with Slimipelago installed, use " +
                   "the mod's in-game Archipelago UI to enter your server address, " +
                   "slot name, and optional password, then start your save. This " +
                   "launcher cannot pre-fill the connection — you enter it in-game.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: guided setup steps ────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Slime Rancher (Steam, appid 433340). Install it if you have not. " +
                "Use \"Select folder...\" above if it was not detected automatically.",
            "2. Install BepInEx 5 (x64 build) into your Slime Rancher folder: download " +
                "the BepInEx 5 release zip from github.com/BepInEx/BepInEx/releases, " +
                "extract it so that SlimeRancher\\BepInEx\\core exists, and launch the " +
                "game once (it will patch itself on first run).",
            "3. Install Slimipelago: use the Install button on the Play tab (it downloads " +
                "the latest release from GitHub and places the plugin DLL in " +
                "SlimeRancher\\BepInEx\\plugins\\Slimipelago), or extract the mod zip " +
                "there manually from the releases page (link below).",
            "4. Launch Slime Rancher from this launcher (or normally). The first launch " +
                "with BepInEx may take a moment as it initialises.",
            "5. In-game, open Slimipelago's Archipelago connection UI, enter your server " +
                "address (host:port), slot name, and optional password, then start your " +
                "save. (This launcher cannot pre-fill the connection — it is entered " +
                "in-game.)",
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
            ("Slimipelago mod releases (GitHub) ↗", ModReleasesPageUrl),
            ("Slimipelago GitHub repo ↗",           ModRepoUrl),
            ("BepInEx 5 releases ↗",                BepInExSite),
            ("BepInEx installation guide ↗",        BepInEx5GuideUrl),
            ("Archipelago Official ↗",              ArchipelagoSite),
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
                Foreground      = new System.Windows.Media.SolidColorBrush(
                                      System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
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
        // Slimipelago GitHub releases are the AP-relevant news for this game.
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
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : $"Slimipelago {ver}",
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: ver,
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var u) ? u.GetString() : ModReleasesPageUrl
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    /// "v1.2.3" → "1.2.3" when leading 'v' decorates a digit; else trimmed as-is.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest Slimipelago release: version string + download URL for
    /// the best matching zip asset. Falls back to the pinned URL when the API is
    /// unreachable or returns no usable asset.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestModAsync(
        CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null &&
                root.TryGetProperty("assets", out var assets) &&
                assets.ValueKind == JsonValueKind.Array)
            {
                // Prefer a zip asset whose name mentions "slimipelago"; then any zip.
                string? preferred = null;
                string? anyZip    = null;

                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u)
                                   ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    anyZip ??= url;
                    if (preferred == null &&
                        (lower.Contains("slimipelago") || lower.Contains("archipelago")))
                        preferred = url;
                }

                string? zip = preferred ?? anyZip;
                if (zip != null)
                    return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (FallbackVersion, FallbackZipUrl);
    }

    // ── Private helpers — Steam / game detection ──────────────────────────────

    /// The Slime Rancher install dir to use: the override (if set and valid) wins,
    /// else the Steam-detected install. Null when nothing is found.
    private string? ResolveGameDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeGameDir(ov))
            return ov;

        try { return DetectSteamGameDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Slime Rancher when it contains SlimeRancher.exe.
    private static bool LooksLikeGameDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, GameExeName));
        }
        catch { return false; }
    }

    /// Detect the Steam Slime Rancher install via registry + libraryfolders.vdf.
    private static string? DetectSteamGameDir()
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

                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeGameDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeGameDir(conventional)) return conventional;
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

        string? hklm64 = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm64)) yield return NormalizeSteamPath(hklm64);

        string? progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

    private static string NormalizeSteamPath(string p)
        => p.Replace('/', '\\').TrimEnd('\\');

    /// All Steam library roots: the Steam root itself plus every "path" entry in
    /// steamapps\libraryfolders.vdf.
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

    /// Pull every "path" value out of a libraryfolders.vdf body (tolerant scan).
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

    /// Read the "installdir" value from an appmanifest_*.acf. Returns null if absent.
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

    /// Find the Slimipelago plugin DLL under the game's BepInEx\plugins tree
    /// (recursive, case-insensitive). Accepts any *.dll whose name mentions
    /// "slimipelago" or a sub-folder named "slimipelago" that holds a DLL.
    /// Returns the path or null.
    private string? FindInstalledModDll()
    {
        try
        {
            string? game = ResolveGameDir();
            if (game == null) return null;

            string pluginsDir = Path.Combine(game, "BepInEx", "plugins");
            if (!Directory.Exists(pluginsDir)) return null;

            // 1. A DLL whose name mentions "slimipelago" anywhere under plugins.
            foreach (string dll in Directory.EnumerateFiles(
                         pluginsDir, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(dll);
                if (name.IndexOf("slimipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                    return dll;
            }

            // 2. A sub-folder whose name mentions "slimipelago" that holds a DLL.
            foreach (string sub in Directory.EnumerateDirectories(
                         pluginsDir, "*", SearchOption.AllDirectories))
            {
                string folder = Path.GetFileName(sub);
                if (folder.IndexOf("slimipelago", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                try
                {
                    if (Directory.EnumerateFiles(sub, "*.dll", SearchOption.AllDirectories).Any())
                        return sub;
                }
                catch { /* permission — keep scanning */ }
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start Slime Rancher: prefer the exe in the detected/override install; fall
    /// back to the steam:// URL when Steam is present but the exe was not found.
    private void StartSlimeRancher()
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
            }) ?? throw new InvalidOperationException("Failed to start Slime Rancher.");

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
            catch { /* non-fatal — some processes don't expose Exited */ }
            return;
        }

        // Fall back to Steam if we know Steam is installed.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true; // best-effort — Steam owns the process
                return;
            }
            catch { /* fall through to the error below */ }
        }

        throw new FileNotFoundException(
            "Could not find \"SlimeRancher.exe\". Open this game's Settings and pick " +
            "your Slime Rancher install folder, or install Slime Rancher via Steam.",
            GameExeName);
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the Slimipelago release zip and extract the BepInEx plugin payload
    /// into <modPluginDir>. The zip typically contains DLL(s) under BepInEx/plugins/
    /// or at the root. We extract to a temp folder, locate the plugin payload
    /// (preferring a BepInEx/plugins sub-tree, then a "plugins" folder, then the
    /// zip root), and copy it into <modPluginDir>.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string modPluginDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"slime-rancher-archipelago-{version}-{Guid.NewGuid():N}.zip");
        string tempDir = Path.Combine(Path.GetTempPath(),
            $"slime-rancher-archipelago-{version}-{Guid.NewGuid():N}");

        try
        {
            progress.Report((10, $"Downloading Slimipelago {version}..."));
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
                        progress.Report((pct,
                            $"Downloading Slimipelago... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((70, "Extracting the mod package..."));
            Directory.CreateDirectory(tempDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempDir, overwriteFiles: true);

            progress.Report((85, "Installing Slimipelago into the plugins folder..."));
            Directory.CreateDirectory(modPluginDir);

            // Locate the plugin payload inside the extracted zip:
            //   1. A BepInEx/plugins sub-tree (canonical mod layout)
            //   2. A top-level "plugins" folder with DLLs inside
            //   3. The extraction root (DLLs alongside manifest.json, etc.)
            string payloadRoot = ResolvePluginPayloadRoot(tempDir);
            CopyDirectoryContents(payloadRoot, modPluginDir);

            progress.Report((95, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip))       File.Delete(tempZip); }             catch { }
            try { if (Directory.Exists(tempDir))   Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// Decide which extracted sub-folder holds the BepInEx plugin payload.
    /// Priority: BepInEx/plugins (deepest DLL-containing folder), then a top-level
    /// "plugins" with a DLL, then the extraction root.
    private static string ResolvePluginPayloadRoot(string extractedRoot)
    {
        try
        {
            // 1. .../BepInEx/plugins (canonical).
            foreach (string dir in Directory.EnumerateDirectories(
                         extractedRoot, "plugins", SearchOption.AllDirectories))
            {
                string parent = Path.GetFileName(
                    Path.GetDirectoryName(dir) ?? "");
                if (parent.Equals("BepInEx", StringComparison.OrdinalIgnoreCase) &&
                    DirectoryHasDll(dir))
                    return dir;
            }

            // 2. A top-level "plugins" folder.
            string topPlugins = Path.Combine(extractedRoot, "plugins");
            if (Directory.Exists(topPlugins) && DirectoryHasDll(topPlugins))
                return topPlugins;
        }
        catch { /* fall through to root */ }

        // 3. The extraction root (DLLs live beside manifest.json, etc.).
        return extractedRoot;
    }

    private static bool DirectoryHasDll(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.dll", SearchOption.AllDirectories).Any();
        }
        catch { return false; }
    }

    /// Recursively copy a directory's contents into a destination folder,
    /// overwriting existing files and creating sub-folders as needed.
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
    // This plugin keeps its launcher-side settings (install-dir override + version
    // stamp) in its own JSON sidecar so it remains a single self-contained file
    // without touching Core/SettingsStore. BOM-less UTF-8, read-modify-write.

    private sealed class SlimeRancherSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private SlimeRancherSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<SlimeRancherSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(SlimeRancherSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(
                SettingsSidecarPath,
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
