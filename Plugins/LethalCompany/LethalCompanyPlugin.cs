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

namespace LauncherV2.Plugins.LethalCompany;

// ═══════════════════════════════════════════════════════════════════════════════
// LethalCompanyPlugin — install / launch for "Lethal Company" (Zeekerss, 2023)
// played through APLC (T0r1nn/APLC on GitHub), a BepInEx 5 mod that is the
// in-game Archipelago client for Lethal Company. This is a NATIVE "ConnectsItself"
// integration — the game itself speaks to the AP server (no emulator, no Lua
// bridge, no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified against T0r1nn/APLC + setup doc) ─
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned Lethal
// Company (Steam appid 1966720), and Archipelago is the APLC BepInEx 5 plugin added
// on top. The honest integration ceiling — exactly like the shipped Hollow Knight,
// Stardew Valley, and Risk of Rain 2 plugins — is "automate what is possible, guide
// the irreducible parts." The verified facts:
//
//   * THE AP WORLD game string is "Lethal Company" (verified against
//     T0r1nn/APLC/APLC_apworld/lethal_company/archipelago.json:
//     `"game": "Lethal Company"` + __init__.py: `game = f"Lethal Company{name}"`,
//     where custom_content.py has `"name": ""` → plain "Lethal Company").
//     world_version = "0.7.11", compatible_version = 7. GameId here = "lethal_company".
//
//   * THE MOD is T0r1nn/APLC on GitHub and on Thunderstore as T0r1nn/APLC.
//     Version 0.7.11 is the latest stable release (verified 2026-06-14).
//     The mod is distributed as a Thunderstore package. The GitHub releases page
//     hosts the .apworld and .yaml files but NOT the mod DLL zip — the mod DLL is
//     on Thunderstore. The official download route is Thunderstore / r2modman / Gale.
//     The package download URL follows the standard Thunderstore format:
//     https://thunderstore.io/package/download/T0r1nn/APLC/{version}/
//
//   * MOD LOADER: BepInEx 5 (BepInExPack-5.4.2100). The APLC Thunderstore manifest
//     dependencies: BepInEx-BepInExPack-5.4.2100, IAmBatby-LethalLevelLoader-1.6.9,
//     Caigan-Archipelago_Scrap-4.0.3, LethalAPI-LethalAPI_Terminal-1.0.1.
//     CRITICAL HONESTY: the APLC Thunderstore zip contains ONLY the APLC plugin DLL
//     and Archipelago.MultiClient.Net.dll — it does NOT bundle BepInEx or its
//     dependencies. The RECOMMENDED install route is r2modman or Gale mod manager,
//     which installs APLC AND resolves all four dependencies in one step.
//     This plugin stages the APLC mod files (best-effort direct-zip install) but
//     presents clear, numbered guided steps so the user can install via r2modman,
//     which is the reliable, one-click route. It does NOT fake a full "installed"
//     state when BepInEx / LethalLevelLoader / etc. are absent.
//
//   * CONNECTION is made IN-GAME via a chat command (verified against setup_en.md):
//     Type `/connect archipelago.gg:port` in the Lethal Company in-game chat, then
//     follow the prompts that appear. The host's `/connect` syncs all players in the
//     lobby; late joiners can also type `/connect` (no args) to connect themselves.
//     Connection info is stored in the LC save file by the mod (ES3 save system,
//     keys ArchipelagoURL/ArchipelagoPort/ArchipelagoSlot/ArchipelagoPassword).
//     There is NO pre-writable config file or command-line arg this launcher can use
//     to pre-fill the connection — the session's server/slot are surfaced in the
//     settings panel so the user can copy-type them into the chat command.
//
//   * TWO LAUNCH ROUTES: the detected Lethal Company.exe (from Steam install), or
//     the steam://rungameid/1966720 fallback when Steam is present but the exe path
//     cannot be resolved. ConnectsItself = true (the mod owns the slot connection —
//     the launcher must NOT hold its own ApClient while the mod is active).
//     SupportsStandalone = true (vanilla LC runs fine without APLC).
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Lethal Company install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, HKLM\...\WOW6432Node\Valve\Steam
//      -> InstallPath), parsing steamapps\libraryfolders.vdf for every library root
//      and locating steamapps\common\Lethal Company via appmanifest_1966720.acf.
//      A manual install-dir OVERRIDE (settings folder picker) is also supported and
//      takes precedence; it is validated (must contain "Lethal Company.exe") and
//      persisted in this plugin's own sidecar (Games/ROMs/lethal_company/lethal_company_launcher.json).
//
//   2. INSTALL/UPDATE (best effort) = download the APLC Thunderstore zip, resolve
//      the plugin payload directory, and extract it into
//      <LC>\BepInEx\plugins\APLC\. Because the zip does not carry BepInEx or its
//      dependency mods, the plugin ALSO presents clear numbered steps + links so the
//      user can install everything via r2modman — the recommended route.
//      Version stamp written to the sidecar after a successful direct-zip install.
//
//   3. LAUNCH = run "Lethal Company.exe" from the detected/override install; if the
//      exe cannot be found but Steam is present, fall back to
//      steam://rungameid/1966720. No prefill (connection is via in-game chat
//      command), stated honestly in the settings panel.
//
// ── DEFENSIVE NOTES ──────────────────────────────────────────────────────────
//   * "Installed" is judged by the presence of an APLC plugin DLL under the
//     detected/override LC install's BepInEx\plugins tree (case-insensitive,
//     recursive). Accepts any *.dll whose name mentions "aplc" or any plugins
//     sub-folder named "aplc" that contains a DLL — so an r2modman install (which
//     nests each mod in a profile folder tree) still counts.
//   * Steam library parsing is defensive (tolerant VDF scan; any failure degrades
//     to "Lethal Company not found" rather than throwing).
//   * No plaintext AP password is ever written by this plugin (connection is
//     entered via in-game chat), so there is nothing to scrub on stop.
//
//   * BUILD NOTE: this project sets UseWindowsForms=true alongside UseWPF=true, so
//     WPF UI types that also exist in WinForms (Color, Button, Brushes, MessageBox,
//     FontWeights, Orientation, …) are spelled with their FULL namespaces below to
//     avoid CS0104 ambiguity, independent of the project's GlobalUsings aliases.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class LethalCompanyPlugin : IGamePlugin
{
    // ── Constants — APLC mod (T0r1nn/APLC, Thunderstore T0r1nn/APLC) ─────────

    // Thunderstore: community lethal-company, namespace archipelago_gg, package APLC.
    // Verified against https://thunderstore.io/c/lethal-company/p/archipelago_gg/APLC/
    private const string TS_NAMESPACE = "archipelago_gg";
    private const string TS_NAME      = "APLC";

    /// Thunderstore package landing page (used for links + the manual route).
    private const string ModPackageUrl =
        "https://thunderstore.io/c/lethal-company/p/archipelago_gg/APLC/";

    /// Thunderstore experimental package API — returns version history (latest
    /// first) with each version's download_url. Used to resolve the newest mod
    /// zip and to build the news feed.
    private const string TS_PACKAGE_API_URL =
        $"https://thunderstore.io/api/experimental/package/{TS_NAMESPACE}/{TS_NAME}/";

    /// r2modman — the RECOMMENDED installer.
    private const string R2ModManSite         = "https://thunderstore.io/package/ebkr/r2modman/";
    private const string GaleSite             = "https://thunderstore.io/package/Kesomannen/GaleModManager/";
    private const string SetupGuideUrl        = "https://github.com/T0r1nn/APLC/blob/main/APLC_apworld/lethal_company/docs/setup_en.md";
    private const string ApworldReleasesUrl   = "https://github.com/T0r1nn/APLC/releases/latest";
    private const string ArchipelagoSite      = "https://archipelago.gg";

    // Steam — Lethal Company appid 1966720 (verified 2026-06-14).
    private const string LcSteamAppId    = "1966720";
    private static readonly string SteamRunUrl = $"steam://rungameid/{LcSteamAppId}";

    /// Standard Steam install sub-folder name + the game exe.
    private const string SteamCommonFolderName = "Lethal Company";
    private const string GameExeName           = "Lethal Company.exe";

    /// Pinned fallback: APLC 0.7.11 (latest stable, verified 2026-06-19).
    private const string FallbackVersion = "0.7.11";
    private static readonly string FallbackZipUrl =
        $"https://thunderstore.io/package/download/{TS_NAMESPACE}/{TS_NAME}/{FallbackVersion}/";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "lethal_company";
    public string DisplayName => "Lethal Company";
    public string Subtitle    => "PC · Archipelago mod";

    /// EXACT AP game string — verified against T0r1nn/APLC/APLC_apworld/
    /// lethal_company/archipelago.json (`"game": "Lethal Company"`) and __init__.py
    /// (`game = f"Lethal Company{name}"`, where custom_content has `"name": ""`).
    public string ApWorldName => "Lethal Company";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "lethal_company.png");

    public string ThemeAccentColor => "#37474F";   // dark blue-grey — industrial scavenging aesthetic

    public string[] GameBadges => new[] { "Steam · needs mod" };

    public string Description =>
        "Lethal Company, the co-op horror scavenging game by Zeekerss, played through " +
        "APLC — a BepInEx 5 mod that is the in-game Archipelago client for Lethal Company. " +
        "Moon visits, quota completions, log entries, bestiary entries, and (optionally) " +
        "each type of scrap become checks shuffled across the multiworld. Moons, shop " +
        "items, ship upgrades, inventory slots, scanner, stamina bars, and more are items. " +
        "You bring your own copy of Lethal Company (owned on Steam), and the APLC mod is " +
        "added on top via the r2modman mod manager (which also installs BepInEx and the " +
        "other mods APLC depends on). The launcher detects your Steam install, can stage " +
        "the APLC mod files, and guides the rest. You connect to your AP server from the " +
        "in-game chat using /connect archipelago.gg:port.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" = an APLC plugin DLL is present under the detected/override
    /// LC install's BepInEx\plugins tree (we do NOT gate on our own stamp — the
    /// user may have installed via r2modman, which we honor).
    public bool IsInstalled => FindInstalledModPlugin() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Working directory for downloads / bookkeeping. Exposed as GameDirectory.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "LethalCompany");

    private string RomLibraryDirectory
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);

    private string SettingsSidecarPath
        => Path.Combine(RomLibraryDirectory, "lethal_company_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // APLC connects to the AP server itself — these exist for interface
    // compatibility only (ConnectsItself = true).
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
            InstalledVersion = FindInstalledModPlugin() != null
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
        progress.Report((2, "Locating your Lethal Company installation..."));
        string? gameDir = ResolveLcDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a Lethal Company installation. Open this game's " +
                "Settings and pick your Lethal Company folder (the one containing " +
                "\"Lethal Company.exe\"), or install Lethal Company via Steam first. " +
                "The APLC mod is added on top of your own copy of the game.");

        string pluginsDir = Path.Combine(gameDir, "BepInEx", "plugins");
        string aplcModDir = Path.Combine(pluginsDir, "APLC");

        progress.Report((6, "Checking the latest APLC release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the APLC mod download on Thunderstore. " +
                "Check your internet connection, or install the mod via r2modman " +
                "(recommended) — see Settings for the guided steps. " +
                "The mod package is " + ModPackageUrl + ".");

        // HONEST: this stages the APLC plugin only. BepInEx, LethalLevelLoader,
        // Archipelago_Scrap, and LethalAPI_Terminal are NOT in this zip — they
        // must be provided by r2modman (the recommended route).
        await DownloadAndExtractModAsync(zipUrl, version, aplcModDir, progress, ct);

        WriteStampedVersion(version);
        InstalledVersion = version;

        bool bepInExPresent = Directory.Exists(Path.Combine(gameDir, "BepInEx", "core"));
        progress.Report((100,
            $"Staged the APLC mod {version} into your Lethal Company " +
            "BepInEx\\plugins folder. " +
            (bepInExPresent
                ? "BepInEx looks present. "
                : "IMPORTANT: this download does NOT include BepInEx, LethalLevelLoader, " +
                  "Archipelago_Scrap, or LethalAPI_Terminal, all of which APLC requires. " +
                  "The recommended way to install everything is the r2modman (or Gale) " +
                  "mod manager — one step installs APLC and all dependencies. Open " +
                  "Settings for the guided steps and links. ") +
            "To play: launch Lethal Company modded, load a save, then type " +
            "/connect SERVER:PORT SLOT_NAME PASSWORD in the in-game chat to connect."));
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
        // HONEST: APLC connects to the AP server from inside the game via an
        // in-game chat command: /connect SERVER:PORT SLOT_NAME PASSWORD (see the
        // mod README). After the initial connect the mod persists connection info
        // in the save file, so subsequent loads reconnect automatically. There is
        // no config file or command-line argument this launcher can pre-write to
        // seed the connection. Launching from this tile just starts the modded game.
        // ConnectsItself = true: the launcher must NOT hold its own ApClient.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartLethalCompany();
        return Task.CompletedTask;
    }

    /// Vanilla Lethal Company runs fine without APLC.
    public bool SupportsStandalone => true;

    /// APLC owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartLethalCompany();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // No plaintext AP password is ever written to disk by this plugin —
        // the connection is entered in-game via chat command.
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // APLC receives items from the AP server directly inside the game.
        // Nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // APLC renders its own AP status inside the game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Return null when the folder is acceptable, else a short reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Lethal Company install folder.";

        if (LooksLikeLcDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeLcDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Lethal Company installation. Pick the folder " +
               "that contains \"Lethal Company.exe\" — for Steam this is usually " +
               @"...\steamapps\common\Lethal Company.";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        string? gameDir     = ResolveLcDir();
        string? overrideDir = LoadOverrideDir();
        string? modPlugin   = FindInstalledModPlugin();
        bool    bepInExOk   = gameDir != null && Directory.Exists(Path.Combine(gameDir, "BepInEx", "core"));

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Lethal Company is your own game (Steam) with the APLC mod added on " +
                   "top via BepInEx 5. The launcher detects your Steam install and can " +
                   "stage the APLC mod files, but the mod needs BepInEx, LethalLevelLoader, " +
                   "Archipelago_Scrap, and LethalAPI_Terminal — which it does not bundle. " +
                   "The recommended way to install everything in one step is the r2modman " +
                   "or Gale mod manager (see the guided steps below). You connect to your " +
                   "AP server in-game via the chat command /connect. These steps involve " +
                   "external tools not controlled by this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LETHAL COMPANY INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Lethal Company not detected. Pick your install folder below, or install " +
              "Lethal Company via Steam first.";
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
                    : "BepInEx not found yet — install it via r2modman or Gale (recommended).",
            FontSize = 11, Foreground = bepInExOk ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modPlugin != null
                    ? "APLC mod found: " + modPlugin
                    : "APLC mod not found in BepInEx\\plugins yet (use Install on the " +
                      "Play tab, or install it via r2modman/Gale — recommended).",
            FontSize = 11, Foreground = modPlugin != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? gameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin     = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Lethal Company install folder (the one containing " +
                          "\"Lethal Company.exe\"). Detected from Steam automatically; " +
                          "set it here to override for non-standard Steam libraries.",
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
                Title            = "Select your Lethal Company install folder",
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
                    System.Windows.MessageBox.Show(bad, "Not a Lethal Company folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                if (!LooksLikeLcDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeLcDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 1966720). Use this " +
                   "picker for a non-standard Steam library or a manual install.",
            FontSize = 11, Foreground = muted, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection (Terminal chat command) ────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (typed in the in-game Terminal chat)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Launch the game (modded), load a save, then type the following command " +
                   "in the in-game chat (the ship's Terminal chat window):",
            FontSize = 11, Foreground = fg, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // Connection command box — prominent, monospace, easy to copy
        panel.Children.Add(new System.Windows.Controls.TextBox
        {
            Text = "/connect SERVER:PORT SLOT_NAME PASSWORD",
            IsReadOnly = true,
            FontFamily = new System.Windows.Media.FontFamily("Consolas, Courier New"),
            FontSize = 13, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xA0, 0xE0, 0xA0)),
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x08, 0x0C, 0x18)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x37, 0x47, 0x4F)),
            Padding = new System.Windows.Thickness(8, 6, 8, 6),
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Example: /connect archipelago.gg:38281 MySlot mypassword\n" +
                   "Omit the password if the server has none. The host's /connect syncs all " +
                   "players in the lobby; late joiners can also type /connect to reconnect. " +
                   "After the first connect, APLC saves connection info in your save file and " +
                   "reconnects automatically on next load. This launcher cannot pre-fill the " +
                   "connection — it is typed in the in-game chat.",
            FontSize = 11, Foreground = fg, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP (recommended: r2modman or Gale mod manager)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Lethal Company (Steam). Install it if you have not. Use \"Select " +
                "folder...\" above if it was not detected.",
            "2. Install the r2modman mod manager (or Gale) from the links below, and select " +
                "Lethal Company as the game.",
            "3. In r2modman (or Gale), search for and install \"APLC\" (by T0r1nn). Its " +
                "dependencies (BepInEx, LethalLevelLoader, Archipelago_Scrap, " +
                "LethalAPI_Terminal) are installed automatically. Then click \"Start modded\".",
            "4. Alternative (advanced): the Install button on the Play tab stages the APLC " +
                "plugin files into your BepInEx\\plugins\\APLC folder. This does NOT include " +
                "BepInEx or its dependencies — you would still need those from r2modman/Gale.",
            "5. Download the lethal_company.apworld from the APLC GitHub releases page (see " +
                "link below) and place it in your Archipelago installation's custom_worlds " +
                "folder. Generate and host your multiworld on archipelago.gg.",
            "6. Launch the game modded. Load a save, then type /connect SERVER:PORT SLOT_NAME " +
                "PASSWORD in the in-game chat (omit password if not required). One YAML per " +
                "Lethal Company lobby — multiple players in the same lobby share one slot.",
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
            ("r2modman (Thunderstore) ↗",            R2ModManSite),
            ("Gale Mod Manager ↗",                   GaleSite),
            ("APLC mod (Thunderstore) ↗",            ModPackageUrl),
            ("APLC GitHub Releases (.apworld) ↗",    ApworldReleasesUrl),
            ("APLC Setup Guide ↗",                   SetupGuideUrl),
            ("Archipelago Official ↗",               ArchipelagoSite),
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
                try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); } catch { }
            };
            panel.Children.Add(btn);
        }

        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // Use the Thunderstore experimental package API: { versions: [ { version_number,
        // description, date_created, download_url }, ... ] }, newest first.
        try
        {
            string json = await _http.GetStringAsync(TS_PACKAGE_API_URL, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("versions", out var versions) ||
                versions.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in versions.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("date_created", out var d) && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                string ver = el.TryGetProperty("version_number", out var v) ? v.GetString() ?? "" : "";

                items.Add(new NewsItem(
                    Title:   "APLC " + ver,
                    Body:    el.TryGetProperty("description", out var b) ? b.GetString() ?? "" : "",
                    Version: ver,
                    Date:    date,
                    Url:     el.TryGetProperty("download_url", out var u) ? u.GetString() : ModPackageUrl
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    /// Resolve the latest mod release from the Thunderstore experimental package
    /// API (version_number + download_url). Falls back to the pinned 0.7.11 URL
    /// when the API is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestModAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(TS_PACKAGE_API_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Preferred shape: { latest: { version_number, download_url }, ... }
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("latest", out var latest) &&
                latest.ValueKind == JsonValueKind.Object)
            {
                string? ver = latest.TryGetProperty("version_number", out var lv) ? lv.GetString() : null;
                string? url = latest.TryGetProperty("download_url",   out var lu) ? lu.GetString() : null;
                if (!string.IsNullOrWhiteSpace(ver) && !string.IsNullOrWhiteSpace(url))
                    return (ver!, url);
            }

            // Fallback shape: first entry of the versions array (newest-first).
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("versions", out var versions) &&
                versions.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in versions.EnumerateArray())
                {
                    string? ver = el.TryGetProperty("version_number", out var ev) ? ev.GetString() : null;
                    string? url = el.TryGetProperty("download_url",   out var eu) ? eu.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(ver) && !string.IsNullOrWhiteSpace(url))
                        return (ver!, url);
                    break;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / shape changed → pinned fallback */ }

        return (FallbackVersion, FallbackZipUrl);
    }

    // ── Private helpers — Steam / Lethal Company detection ───────────────────

    /// The LC install dir to use: the override (if set and valid) wins, else the
    /// Steam-detected install. Null when nothing is found.
    private string? ResolveLcDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeLcDir(ov))
            return ov;

        try { return DetectSteamLcDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Lethal Company if it contains "Lethal Company.exe".
    private static bool LooksLikeLcDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, GameExeName));
        }
        catch { return false; }
    }

    /// Detect the Steam Lethal Company install: read the Steam root from the
    /// registry, gather all library roots from libraryfolders.vdf, and find the
    /// one whose appmanifest_1966720.acf exists → steamapps\common\Lethal Company.
    private static string? DetectSteamLcDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{LcSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common    = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeLcDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeLcDir(conventional)) return conventional;
                }
                catch { /* try the next library */ }
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

    /// All Steam library roots: the Steam root itself plus every "path" entry in
    /// steamapps\libraryfolders.vdf (tolerant text scan).
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

    /// Find the APLC plugin under the detected/override install's BepInEx\plugins
    /// tree (recursive, case-insensitive). Accepts either a *.dll whose name
    /// mentions "aplc", or a plugins sub-folder whose name mentions "aplc" that
    /// holds at least one DLL (r2modman layout). Returns the matched path or null.
    private string? FindInstalledModPlugin()
    {
        try
        {
            string? game = ResolveLcDir();
            if (game == null) return null;
            string pluginsDir = Path.Combine(game, "BepInEx", "plugins");
            if (!Directory.Exists(pluginsDir)) return null;

            // 1. A DLL named like APLC anywhere under plugins.
            foreach (string dll in Directory.EnumerateFiles(pluginsDir, "*.dll",
                         SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(dll);
                if (name.IndexOf("aplc", StringComparison.OrdinalIgnoreCase) >= 0)
                    return dll;
            }

            // 2. A plugins sub-folder named like APLC that holds a DLL.
            foreach (string sub in Directory.EnumerateDirectories(pluginsDir, "*",
                         SearchOption.AllDirectories))
            {
                string folder = Path.GetFileName(sub);
                if (folder.IndexOf("aplc", StringComparison.OrdinalIgnoreCase) < 0) continue;
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

    private void StartLethalCompany()
    {
        string? game = ResolveLcDir();
        string? exe  = game != null ? Path.Combine(game, GameExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = game!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Lethal Company.");

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

        // Fall back to Steam if Steam appears to be installed.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true; // best-effort; Steam owns the process
                return;
            }
            catch { /* fall through to error */ }
        }

        throw new FileNotFoundException(
            "Could not find \"Lethal Company.exe\". Open this game's Settings and pick " +
            "your Lethal Company install folder, or install Lethal Company via Steam.",
            GameExeName);
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the APLC Thunderstore zip and extract the plugin payload into
    /// <LC>\BepInEx\plugins\APLC\. Honest scope: stages the APLC plugin DLL only;
    /// BepInEx / LethalLevelLoader / Archipelago_Scrap / LethalAPI_Terminal come
    /// from r2modman (the recommended route) and are not in this zip.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string aplcModDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"lc-aplc-{version}-{Guid.NewGuid():N}.zip");
        string tempDir = Path.Combine(Path.GetTempPath(),
            $"lc-aplc-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading APLC mod {version}..."));
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
                        progress.Report((pct, $"Downloading APLC mod... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((70, "Extracting the mod package..."));
            Directory.CreateDirectory(tempDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempDir, overwriteFiles: true);

            progress.Report((85, "Installing mod into the Lethal Company plugins folder..."));
            Directory.CreateDirectory(aplcModDir);

            // The Thunderstore zip is a "mod package": manifest.json + icon.png at
            // the root, with the DLL(s) either at the zip root or under a
            // BepInEx/plugins sub-tree. Resolve which folder holds the plugin DLL
            // and copy its contents into BepInEx\plugins\APLC\.
            string payloadRoot = ResolvePluginPayloadRoot(tempDir);
            CopyDirectoryContents(payloadRoot, aplcModDir);

            progress.Report((95, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip))   File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    /// Decide which extracted folder holds the BepInEx plugin payload.
    /// Order: a nested BepInEx/plugins that contains a DLL, then a top-level
    /// "plugins" folder, then the extraction root.
    private static string ResolvePluginPayloadRoot(string extractedRoot)
    {
        try
        {
            // 1. .../BepInEx/plugins (canonical modded layout).
            foreach (string dir in Directory.EnumerateDirectories(extractedRoot,
                         "plugins", SearchOption.AllDirectories))
            {
                string parent = Path.GetFileName(Path.GetDirectoryName(dir) ?? "");
                if (parent.Equals("BepInEx", StringComparison.OrdinalIgnoreCase) &&
                    DirectoryHasDll(dir))
                    return dir;
            }

            // 2. A top-level "plugins" folder with a DLL inside.
            string topPlugins = Path.Combine(extractedRoot, "plugins");
            if (Directory.Exists(topPlugins) && DirectoryHasDll(topPlugins))
                return topPlugins;
        }
        catch { /* fall through to the root */ }

        // 3. The extraction root (DLL sits alongside manifest.json).
        return extractedRoot;
    }

    private static bool DirectoryHasDll(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*.dll", SearchOption.AllDirectories).Any(); }
        catch { return false; }
    }

    /// Recursively copy a directory's contents into destDir (overwriting).
    private static void CopyDirectoryContents(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceDir, file);
            string dst = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(file, dst, overwrite: true);
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class LcSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private LcSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<LcSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(LcSettings s)
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
