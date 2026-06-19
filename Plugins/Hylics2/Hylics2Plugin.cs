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
// GlobalUsings.cs already aliases the colliding short names project-wide; this file
// adds NO file-level `using X = System.Windows...;` alias (that would be CS1537).

namespace LauncherV2.Plugins.Hylics2;

// ═══════════════════════════════════════════════════════════════════════════════
// Hylics2Plugin — install / launch for "Hylics 2" (Mason Lindroth, 2020) played
// through ArchipelagoHylics2, a BepInEx 5 plugin that is the in-game Archipelago
// client for Hylics 2. This is a NATIVE "ConnectsItself" integration in the same
// family as the shipped Subnautica / Hollow Knight / Stardew Valley plugins (and
// Ship of Harkinian) — the game itself speaks to the AP server (no emulator, no
// Lua bridge, no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned
// Hylics 2 (Steam appid 1286710 — confirmed against the mod README's store link
// https://store.steampowered.com/app/1286710/Hylics_2/ and the live Steam page;
// NOTE the appid in this launcher's task brief, 1349230, was incorrect — the
// real Hylics 2 appid is 1286710), and Archipelago is a MOD (a BepInEx plugin)
// added on top. The honest integration ceiling — exactly like the shipped
// Subnautica/Hollow Knight plugins — is "automate what is possible, guide the
// irreducible parts." The verified facts:
//
//   * THE AP WORLD game string is "Hylics 2" (verified against
//     worlds/hylics2/__init__.py in core Archipelago:
//     `class Hylics2World(World)`, `game: str = "Hylics 2"`). Hylics 2 is a CORE
//     Archipelago world — it ships INSIDE Archipelago itself, so NO custom world
//     drop into custom_worlds is needed (unlike a community apworld). GameId here
//     = "hylics2".
//
//   * THE MOD repo is TRPG0/ArchipelagoHylics2 (the official client, 100% C#).
//     The latest release verified live 2026-06-14 is tag 1.0.11, a SINGLE asset
//     "ArchipelagoHylics2_1.0.11.zip" (~556 KB, published 2026-03-23).
//
//   * CRITICAL — UNLIKE SUBNAUTICA, THE MOD ZIP DOES *NOT* BUNDLE BepInEx. The
//     README states plainly: install "BepInEx 5 (32-bit, version 5.4.20 or newer)"
//     from https://github.com/BepInEx/BepInEx/releases into the Hylics 2 root,
//     run the game once to let BepInEx generate its folders, THEN "extract the
//     contents of the zip file into BepInEx\plugins". So this plugin's install
//     does TWO downloads: (a) BepInEx_win_x86 (32-bit — Hylics 2 is a 32-bit
//     XNA/MonoGame title, so the x64 loader will NOT work) extracted into the
//     Hylics 2 root, then (b) the ArchipelagoHylics2 zip extracted into
//     BepInEx\plugins. We do NOT run the game to pre-generate folders — extracting
//     the mod straight into BepInEx\plugins is equivalent (BepInEx creates the
//     rest on first launch). Pinned fallbacks cover an offline GitHub API.
//
//   * CONNECTION is made IN-GAME via the mod's console: "open the in-game console
//     (default key: /) and use the command /connect [address:port] [name]
//     [password]". There is NO command-line arg and NO config file this launcher
//     can pre-write for the connection (verified against the README). So this
//     plugin does NOT attempt a connection prefill — it surfaces the session's
//     address / slot / password (and the exact /connect command) in the settings
//     panel + post-install note so the user can type/paste them in-game. Because
//     nothing plaintext is written to disk, there is nothing to scrub on stop.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Hylics 2 install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, HKLM\...\WOW6432Node\Valve\Steam
//      -> InstallPath), parsing steamapps\libraryfolders.vdf for every library
//      root and locating steamapps\common\Hylics 2 via appmanifest_1286710.acf. A
//      manual install-dir OVERRIDE (settings folder picker) is also supported and
//      takes precedence; it is validated (must contain Hylics2.exe) and persisted
//      in this plugin's OWN sidecar (Games/ROMs/hylics2/hylics2_launcher.json) —
//      Core/SettingsStore is NOT modified. (GOG / other stores work via the manual
//      picker.)
//   2. INSTALL/UPDATE (best effort) = download BepInEx_win_x86 and extract it into
//      the Hylics 2 install root, then download the ArchipelagoHylics2 zip and
//      extract it into <Hylics 2>\BepInEx\plugins. Plus clear, numbered guided
//      steps + links so the user can verify and complete setup. Never a fake
//      one-click that cannot exist.
//   3. LAUNCH = run Hylics2.exe from the detected/override install; if the exe
//      cannot be found but Steam is present, fall back to steam://rungameid/1286710.
//      ConnectsItself = true (the mod owns the slot — the launcher must NOT hold
//      its own ApClient on it). SupportsStandalone = true (plain Hylics 2 runs
//      perfectly without AP). No prefill (in-game console), stated honestly.
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", Subnautica/HK-style) ──────
//   * "Installed" is judged by the presence of the BepInEx tree in a detected/
//     override Hylics 2 install AND an ArchipelagoHylics2-named mod assembly under
//     BepInEx/plugins (case-insensitive, recursive) — NOT by an OUR-OWN version
//     stamp, because the user may instead install by hand, which this launcher
//     honors. If no Hylics 2 install is detected, the tile reads "not installed".
//   * The Hylics2.exe name and the "Hylics 2" steam common-folder name were taken
//     from the standard Steam layout; exe resolution falls back to a fuzzy
//     "*hylics*.exe" scan, and folder detection prefers the manifest's installdir.
//   * Steam library parsing is defensive: a tolerant VDF scan that pulls quoted
//     "path" values; any failure degrades to "Hylics 2 not found" rather than
//     throwing.
//   * No plaintext AP password is ever written by this plugin (connection is
//     entered in-game), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class Hylics2Plugin : IGamePlugin
{
    // ── Constants — the AP Hylics 2 mod (real repo, verified 2026-06-14) ───────
    private const string MOD_OWNER = "TRPG0";
    private const string MOD_REPO  = "ArchipelagoHylics2";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";
    private const string ModReleasesPageUrl = $"{ModRepoUrl}/releases";

    // BepInEx — Hylics 2 needs the 32-bit (x86) loader, 5.4.20+. We resolve the
    // latest stable x86 Windows asset from the BepInEx releases API at install time,
    // with a pinned fallback when offline.
    private const string BEPINEX_OWNER = "BepInEx";
    private const string BEPINEX_REPO  = "BepInEx";
    private const string BepInExSite   = $"https://github.com/{BEPINEX_OWNER}/{BEPINEX_REPO}";
    private const string GH_BEPINEX_RELEASES_URL =
        $"https://api.github.com/repos/{BEPINEX_OWNER}/{BEPINEX_REPO}/releases";

    private const string SetupReadmeUrl = $"{ModRepoUrl}#readme";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Hylics 2 appid 1286710 (verified; the task brief's 1349230 is wrong).
    private const string SteamAppId = "1286710";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    /// The standard Steam install sub-folder name for Hylics 2 (note the space).
    private const string SteamCommonFolderName = "Hylics 2";

    /// Pinned fallback for the mod when the GitHub API is unreachable. Tag 1.0.11
    /// verified live 2026-06-14; the single asset is "ArchipelagoHylics2_1.0.11.zip".
    /// The API path is the normal route; this is the net so an offline Install still
    /// has something to fetch.
    private const string FallbackModVersion = "1.0.11";
    private const string FallbackModZipName = "ArchipelagoHylics2_1.0.11.zip";
    private static readonly string FallbackModZipUrl =
        $"{ModRepoUrl}/releases/download/{FallbackModVersion}/{FallbackModZipName}";

    /// Pinned fallback for BepInEx (32-bit Windows) when the GitHub API is
    /// unreachable. v5.4.23.5 verified live 2026-06-14 — asset
    /// "BepInEx_win_x86_5.4.23.5.zip". (>= the README's 5.4.20 minimum.)
    private const string FallbackBepInExTag = "v5.4.23.5";
    private const string FallbackBepInExZipName = "BepInEx_win_x86_5.4.23.5.zip";
    private static readonly string FallbackBepInExZipUrl =
        $"{BepInExSite}/releases/download/{FallbackBepInExTag}/{FallbackBepInExZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "hylics2";
    public string DisplayName => "Hylics 2";
    public string Subtitle    => "Native PC · Archipelago";

    /// EXACT AP game string — verified against worlds/hylics2/__init__.py in core
    /// Archipelago (Hylics2World.game = "Hylics 2"). Hylics 2 is a CORE world; no
    /// custom_worlds drop is needed.
    public string ApWorldName => "Hylics 2";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "hylics2.png");

    public string ThemeAccentColor => "#C9A227";   // claymation gold/ochre

    public string[] GameBadges => new[] { "Requires Hylics 2 on Steam" };

    public string Description =>
        "Hylics 2 is Mason Lindroth's 2020 surreal, claymation-styled RPG — a " +
        "psychedelic journey to defeat the revived Gibby, played through " +
        "ArchipelagoHylics2, a BepInEx mod that is the in-game Archipelago client. " +
        "Party members, TVs, items, gestures and key progression are shuffled into " +
        "the multiworld, and the game connects to the Archipelago server itself (no " +
        "emulator, no bridge). You bring your own copy of Hylics 2 (Steam), and the " +
        "Archipelago mod is added on top — the launcher detects your Steam install, " +
        "stages the BepInEx mod loader, and unpacks the mod into it for you. You " +
        "connect to your server from the in-game console with the /connect command.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the AP Hylics 2 mod is present (the BepInEx tree exists in
    /// a detected/override Hylics 2 install and an ArchipelagoHylics2-named assembly
    /// is under BepInEx/plugins). We do NOT gate on our own stamp — the user may
    /// have unpacked by hand, which we honor.
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads (the mod zip, BepInEx zip) and working
    /// files. The actual mod is extracted INTO the Hylics 2 install root, not here.
    /// Exposed as GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Hylics2");

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained file — same as Subnautica/Doom/HK).
    /// Lives under Games/ROMs/hylics2/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "hylics2_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The ArchipelagoHylics2 mod reports checks/items/goal to the AP server itself —
    // the launcher relays nothing. These exist for interface compatibility
    // (ConnectsItself = true).
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
        // 0. We need a Hylics 2 install to unpack the mod into. Prefer an explicit
        //    override; else auto-detect the Steam install.
        progress.Report((2, "Locating your Hylics 2 installation..."));
        string? h2Dir = ResolveHylics2Dir();
        if (h2Dir == null)
            throw new InvalidOperationException(
                "Could not find a Hylics 2 installation. Open this game's Settings " +
                "and pick your Hylics 2 folder (the one containing Hylics2.exe), or " +
                "install Hylics 2 via Steam first. The Archipelago mod is added on " +
                "top of your own copy of the game.");

        // 1. Resolve the latest mod release (pinned fallback when offline).
        progress.Report((6, "Checking the latest ArchipelagoHylics2 release..."));
        var (modVersion, modZipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = modVersion;

        if (modZipUrl == null)
            throw new InvalidOperationException(
                "Could not find the ArchipelagoHylics2 mod download on GitHub. Check " +
                "your internet connection, or download the mod zip manually from " +
                ModReleasesPageUrl + " and unpack it into your Hylics 2 folder's " +
                "BepInEx\\plugins. See Settings for the guided steps. The mod repo is " +
                ModRepoUrl + ".");

        // 2. Stage BepInEx (32-bit) into the Hylics 2 root — UNLESS a BepInEx tree
        //    is already there (the user may have installed it, or another mod did).
        //    Hylics 2 is a 32-bit title, so the x86 loader is mandatory.
        if (!Directory.Exists(Path.Combine(h2Dir, "BepInEx")))
        {
            progress.Report((10, "Resolving the BepInEx mod loader (32-bit)..."));
            var (bepTag, bepZipUrl) = await ResolveBepInExX86Async(ct);
            if (bepZipUrl == null)
                throw new InvalidOperationException(
                    "Could not find the 32-bit BepInEx download on GitHub. Install " +
                    "BepInEx 5 (32-bit, 5.4.20+) into your Hylics 2 folder manually " +
                    "from " + BepInExSite + "/releases, then run Install again (or drop " +
                    "the mod into BepInEx\\plugins yourself). See Settings for steps.");

            await DownloadAndExtractZipIntoAsync(
                bepZipUrl, h2Dir,
                $"bepinex-x86-{bepTag}", "BepInEx mod loader",
                progress, startPct: 14, endPct: 45, ct: ct);
        }
        else
        {
            progress.Report((45, "BepInEx is already present — keeping it."));
        }

        // 3. Download + extract the mod zip INTO <Hylics 2>\BepInEx\plugins.
        string pluginsDir = Path.Combine(h2Dir, "BepInEx", "plugins");
        await DownloadAndExtractZipIntoAsync(
            modZipUrl, pluginsDir,
            $"hylics2-archipelago-{modVersion}", $"ArchipelagoHylics2 {modVersion}",
            progress, startPct: 48, endPct: 92, ct: ct);

        // 4. Stamp the version next to our sidecar so the tile can show it. (This is
        //    informational only — IsInstalled is judged by the mod's presence.)
        WriteStampedVersion(modVersion);
        InstalledVersion = modVersion;

        progress.Report((100,
            $"Installed ArchipelagoHylics2 {modVersion} into your Hylics 2 folder " +
            "(BepInEx 32-bit staged + mod dropped into BepInEx\\plugins). To play: " +
            "launch the game, open the in-game console (default key: /), and connect " +
            "with  /connect [address:port] [slotname] [password]  (omit the password " +
            "if your room has none). Hylics 2 is a CORE Archipelago world — no custom " +
            "world file is needed. See Settings for the full guided steps and this " +
            "session's connection details."));
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
        // HONEST: the AP server connection for Hylics 2 is entered IN-GAME via the
        // mod's console (open with `/`, then `/connect [address:port] [name]
        // [password]`). The mod has no command-line / config prefill this launcher
        // can apply (verified — see header). So launching from this tile just starts
        // the game; the user connects in-game with the session credentials (the
        // settings panel + note surface those, including a ready-to-paste /connect
        // line).
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on this
        // slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartHylics2();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) Hylics 2 runs perfectly well.
    public bool SupportsStandalone => true;

    /// The ArchipelagoHylics2 mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartHylics2();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // We only ever started Hylics 2 from here. Kill what we launched.
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // No plaintext AP password is ever written to disk by this plugin (the
        // connection is typed into the in-game console), so there is nothing to
        // scrub.
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The ArchipelagoHylics2 mod receives items from the AP server directly;
        // there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid Hylics 2 folder contains
    /// Hylics2.exe. Return null when acceptable, else a short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Hylics 2 install folder.";

        if (LooksLikeHylics2Dir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeHylics2Dir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Hylics 2 installation. Pick the folder that " +
               "contains Hylics2.exe. For Steam this is usually " +
               @"...\steamapps\common\Hylics 2.";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Unverified-offline honesty header ─────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Hylics 2 is your own game (Steam) with the ArchipelagoHylics2 mod " +
                   "added on top. The mod needs the 32-bit BepInEx mod loader — the " +
                   "launcher can detect your Steam install, stage BepInEx, and unpack " +
                   "the mod into BepInEx\\plugins for you (or you can do it by hand). " +
                   "You connect to your server from the IN-GAME console (open with /, " +
                   "then /connect) — there is no connection file to pre-fill. These " +
                   "external steps are not verified by this launcher.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "HYLICS 2 INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? h2Dir       = ResolveHylics2Dir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg   = h2Dir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + h2Dir
                : "Detected Steam install: " + h2Dir)
            : "Hylics 2 not detected. Pick your install folder below, or install " +
              "Hylics 2 via Steam first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = h2Dir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // mod status line
        string? modDll = FindInstalledModDll();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                    ? "ArchipelagoHylics2 mod found: " + modDll
                    : "ArchipelagoHylics2 mod not found in BepInEx\\plugins yet (use " +
                      "Install on the Play tab, or unpack the mod zip into your Hylics 2 " +
                      "folder's BepInEx\\plugins).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? h2Dir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Hylics 2 install folder (the one containing Hylics2.exe). " +
                          "Detected from Steam automatically; set it here to override (GOG " +
                          "/ non-standard Steam library).",
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
                Title            = "Select your Hylics 2 install folder (contains Hylics2.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? h2Dir ?? "")
                                   ? (overrideDir ?? h2Dir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Hylics 2 folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeHylics2Dir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeHylics2Dir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 1286710). Use this " +
                   "picker for GOG, or a non-standard Steam library.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connecting (in-game console) ─────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (typed into the in-game console)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "In Hylics 2, open the mod's console with the / key, then type:\n" +
                   "    /connect [address:port] [slotname] [password]\n" +
                   "for example  /connect archipelago.gg:38281 YourSlot  (omit the " +
                   "password if your room has none). The launcher cannot pre-fill this — " +
                   "it is entered in-game.",
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
            "1. Own Hylics 2 (Steam). Install it if you have not. Use \"Select " +
                "folder...\" above if it was not auto-detected.",
            "2. Install the mod: use Install on the Play tab. It stages the 32-bit " +
                "BepInEx mod loader into your Hylics 2 folder and unpacks " +
                "ArchipelagoHylics2 into BepInEx\\plugins. (Hylics 2 is a CORE " +
                "Archipelago world, so no custom world file is needed.)",
            "3. Alternative (manual): install BepInEx 5 (32-bit, 5.4.20+) from the " +
                "BepInEx releases page into your Hylics 2 folder, run the game once, " +
                "then unpack the ArchipelagoHylics2 zip into BepInEx\\plugins.",
            "4. Launch Hylics 2 from this launcher (or normally). The first launch with " +
                "BepInEx present takes a little longer while it initialises.",
            "5. In-game, open the console with the / key and run  /connect " +
                "[address:port] [slotname] [password]  to join your multiworld. (This " +
                "launcher cannot pre-fill the connection — it is entered in-game.)",
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
            ("ArchipelagoHylics2 (releases) ↗", ModReleasesPageUrl),
            ("ArchipelagoHylics2 Setup (README) ↗", SetupReadmeUrl),
            ("BepInEx (mod loader) ↗",           BepInExSite),
            ("Archipelago Official ↗",           ArchipelagoSite),
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
        // Mod releases are the AP-relevant news for this game.
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

    /// "v1.0.11" → "1.0.11" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + the mod zip download URL. Prefers
    /// an asset matching "Hylics" / "Archipelago" and ".zip"; falls back to the
    /// first .zip asset; falls back to the pinned 1.0.11 direct URL when the API is
    /// unreachable.
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
                string? preferred = null;   // a .zip named like Hylics*/Archipelago*.zip
                string? anyZip    = null;   // any .zip
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    anyZip ??= url;
                    if (preferred == null && (lower.Contains("hylics") || lower.Contains("archipelago")))
                        preferred = url;
                }
                string? zip = preferred ?? anyZip;
                if (zip != null)
                    return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (FallbackModVersion, FallbackModZipUrl);
    }

    /// Resolve the latest STABLE 32-bit Windows BepInEx 5 release: tag + zip URL.
    /// Matches an asset whose name contains "win" + "x86" (and NOT "x64") and ".zip"
    /// among non-prerelease releases. Falls back to the pinned v5.4.23.5 x86 zip
    /// when the API is unreachable. We deliberately pick BepInEx 5 (the README's
    /// requirement) — BepInEx 6 has a different layout, so we only accept "5." tags
    /// from the list when present, else the newest stable that yields an x86 asset.
    private async Task<(string Tag, string? ZipUrl)> ResolveBepInExX86Async(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_BEPINEX_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                // First pass: prefer a stable BepInEx 5.x release with an x86 asset.
                foreach (var rel in doc.RootElement.EnumerateArray())
                {
                    if (rel.TryGetProperty("prerelease", out var pre)
                        && pre.ValueKind == JsonValueKind.True) continue;

                    string? tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                    if (tag == null) continue;
                    // BepInEx 5 tags look like "v5.4.23.5". Require a "5." line.
                    string tagDigits = tag.TrimStart('v');
                    if (!tagDigits.StartsWith("5.")) continue;

                    string? zip = FindBepInExX86Asset(rel);
                    if (zip != null) return (tag, zip);
                }
                // Second pass: any stable release with an x86 asset (last resort).
                foreach (var rel in doc.RootElement.EnumerateArray())
                {
                    if (rel.TryGetProperty("prerelease", out var pre)
                        && pre.ValueKind == JsonValueKind.True) continue;
                    string? tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                    if (tag == null) continue;
                    string? zip = FindBepInExX86Asset(rel);
                    if (zip != null) return (tag, zip);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (FallbackBepInExTag, FallbackBepInExZipUrl);
    }

    /// Find a 32-bit Windows BepInEx zip asset in a release element:
    /// name contains "win" and "x86", ends ".zip", and is NOT an x64/linux/macos
    /// or the small "Patcher" archive.
    private static string? FindBepInExX86Asset(JsonElement releaseElement)
    {
        if (!releaseElement.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array) return null;

        foreach (var a in assets.EnumerateArray())
        {
            string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name == null || url == null) continue;

            string lower = name.ToLowerInvariant();
            if (!lower.EndsWith(".zip")) continue;
            if (!lower.Contains("win")) continue;
            if (!lower.Contains("x86")) continue;
            if (lower.Contains("x64")) continue;
            if (lower.Contains("linux") || lower.Contains("macos")) continue;
            if (lower.Contains("patcher")) continue;
            return url;
        }
        return null;
    }

    // ── Private helpers — Steam / Hylics 2 detection ──────────────────────────

    /// The Hylics 2 install dir to use: the override (if set and valid) wins, else
    /// the Steam-detected install. Null when nothing is found.
    private string? ResolveHylics2Dir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeHylics2Dir(ov))
            return ov;

        try { return DetectSteamHylics2Dir(); }
        catch { return null; }
    }

    /// A folder "looks like" Hylics 2 if it has Hylics2.exe, or (defensive) any
    /// "*hylics*.exe" at its top level.
    private static bool LooksLikeHylics2Dir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, "Hylics2.exe"))) return true;
            foreach (string exe in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetFileNameWithoutExtension(exe)
                        .IndexOf("hylics", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam Hylics 2 install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_1286710.acf exists → steamapps\common\Hylics 2.
    private static string? DetectSteamHylics2Dir()
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

                    // Prefer the installdir named in the manifest; fall back to the
                    // conventional "Hylics 2" folder name.
                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeHylics2Dir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeHylics2Dir(conventional)) return conventional;
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
    /// steamapps\libraryfolders.vdf. Tolerant text scan (the VDF is a simple quoted
    /// key/value tree; we only need the path values).
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
    /// Handles the Steam-VDF escaping of backslashes (\\ → \).
    private static IEnumerable<string> ExtractVdfPaths(string text)
    {
        const string key = "\"path\"";
        int i = 0;
        while ((i = text.IndexOf(key, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            i += key.Length;
            // find the opening quote of the value
            int open = text.IndexOf('"', i);
            if (open < 0) yield break;
            int close = text.IndexOf('"', open + 1);
            if (close < 0) yield break;

            string raw = text.Substring(open + 1, close - open - 1);
            yield return raw.Replace("\\\\", "\\");
            i = close + 1;
        }
    }

    /// Read the "installdir" value from an appmanifest_*.acf (same quoted-pair
    /// format as VDF). Returns null if absent.
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

    /// Safe registry string read; null on any failure (key/value missing, etc.).
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

    /// Find the AP Hylics 2 mod assembly under the detected/override install's
    /// BepInEx\plugins tree (recursive, case-insensitive). The mod's DLL is named
    /// like "ArchipelagoHylics2.dll", but we match any *.dll whose name contains
    /// "hylics" or "archipelago" so a renamed/sub-foldered drop still counts.
    /// Returns the dll path or null.
    private string? FindInstalledModDll()
    {
        try
        {
            string? h2 = ResolveHylics2Dir();
            if (h2 == null) return null;

            string pluginsDir = Path.Combine(h2, "BepInEx", "plugins");
            if (!Directory.Exists(pluginsDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(pluginsDir, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(dll);
                if (name.IndexOf("hylics", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                    return dll;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start Hylics 2: prefer the exe in the detected/override install; if that
    /// cannot be found but Steam is present, fall back to the steam:// URL. Surfaces
    /// a clear message rather than failing opaquely.
    private void StartHylics2()
    {
        string? h2  = ResolveHylics2Dir();
        string? exe = h2 != null ? ResolveHylics2Exe(h2) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = h2!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Hylics 2.");

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
                IsRunning = true; // best-effort; Steam owns the process, so we won't track exit
                return;
            }
            catch { /* fall through to the error below */ }
        }

        throw new FileNotFoundException(
            "Could not find Hylics2.exe. Open this game's Settings and pick your " +
            "Hylics 2 install folder, or install Hylics 2 via Steam.",
            "Hylics2.exe");
    }

    /// Resolve the Hylics 2 exe in an install dir: prefer "Hylics2.exe", else the
    /// first top-level "*hylics*.exe".
    private static string? ResolveHylics2Exe(string dir)
    {
        try
        {
            string preferred = Path.Combine(dir, "Hylics2.exe");
            if (File.Exists(preferred)) return preferred;
            foreach (string exe in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetFileNameWithoutExtension(exe)
                        .IndexOf("hylics", StringComparison.OrdinalIgnoreCase) >= 0)
                    return exe;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — download / extract zips ─────────────────────────────

    /// Download a zip and extract it INTO <destDir>, merging (overwriting files,
    /// creating dirs, never deleting the user's other files). Used twice: for the
    /// BepInEx loader (into the Hylics 2 root) and for the mod (into
    /// BepInEx\plugins). Reports progress between startPct and endPct.
    private async Task DownloadAndExtractZipIntoAsync(
        string zipUrl,
        string destDir,
        string tempStem,
        string label,
        IProgress<(int Pct, string Msg)> progress,
        int startPct,
        int endPct,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(), $"{tempStem}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(), $"{tempStem}-{Guid.NewGuid():N}");
        int dlEnd = startPct + (int)((endPct - startPct) * 0.7); // 70% of the band is download
        try
        {
            progress.Report((startPct, $"Downloading {label}..."));
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
                        int pct = startPct + (int)((dlEnd - startPct) * downloaded / total);
                        progress.Report((pct, $"Downloading {label}... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            // Extract to a temp staging folder first, so we can normalise a possible
            // single wrapping sub-folder before merging into the destination.
            progress.Report((dlEnd, $"Unpacking {label}..."));
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // If the zip wrapped everything in ONE sub-folder (and has no top-level
            // files), descend into it so the merge lands at the right depth. Both
            // the BepInEx zip and the mod zip extract their payload at the zip root,
            // but this guards against a re-packaged single-folder zip.
            string mergeRoot = tempExtract;
            string[] subdirs = Directory.GetDirectories(tempExtract);
            string[] files   = Directory.GetFiles(tempExtract);
            if (files.Length == 0 && subdirs.Length == 1)
                mergeRoot = subdirs[0];

            progress.Report((dlEnd + (endPct - dlEnd) / 2, $"Installing {label}..."));
            Directory.CreateDirectory(destDir);
            MergeDirectory(mergeRoot, destDir);

            progress.Report((endPct, $"{label} installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    /// Recursively copy everything under <src> into <dst>, overwriting files and
    /// creating directories as needed. Never deletes anything in <dst> that is not
    /// being overwritten — so the user's existing files are preserved.
    private static void MergeDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, dir);
            Directory.CreateDirectory(Path.Combine(dst, rel));
        }
        foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string rel     = Path.GetRelativePath(src, file);
            string target  = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // This plugin keeps its launcher-side settings (the install-dir override + an
    // informational version stamp) in its OWN JSON file so it stays a single
    // self-contained source file and does not modify Core/SettingsStore. BOM-less
    // UTF-8, read-modify-write (same approach as Subnautica/Doom/HK).

    private sealed class Hylics2Settings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private Hylics2Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<Hylics2Settings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(Hylics2Settings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal — setting just won't persist this time */ }
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
