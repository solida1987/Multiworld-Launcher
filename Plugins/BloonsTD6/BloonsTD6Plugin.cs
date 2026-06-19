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
// FontWeights / Clipboard collide between System.Windows.Forms and
// System.Windows[.Controls/.Media] (CS0104). Qualifying every UI type avoids that.

namespace LauncherV2.Plugins.BloonsTD6;

// ═══════════════════════════════════════════════════════════════════════════════
// BloonsTD6Plugin — install / launch for "Bloons TD 6" (Ninja Kiwi, 2018)
// played through BloonsArchipelago, a MelonLoader / BTD6 Mod Helper mod that is
// the in-game Archipelago client for Bloons TD 6. This is a NATIVE "ConnectsItself"
// integration — the game itself speaks to the AP server (no emulator, no Lua
// bridge, no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned
// Bloons TD 6 (Steam appid 960090; the game is also on Epic Games Store, iOS and
// Android, but the AP mod only works on the PC Steam version). The Archipelago
// integration is a CUSTOM AP WORLD maintained in a fork:
//   github.com/GamingInfinite/Archipelago   (the apworld, worlds/bloonstd6/)
//
// The verified facts:
//
//   * THE AP WORLD game string is "Bloons TD6" (verified against
//     worlds/bloonstd6/__init__.py: `game = "Bloons TD6"`). GameId here = "btd6".
//     The apworld file is "bloonstd6.apworld", shipped with each release of the
//     GamingInfinite/Archipelago fork (latest: v0.3.3.0, asset "bloonstd6.apworld").
//     The apworld must be placed into the user's local Archipelago installation's
//     "lib/worlds/" folder — this is NOT done by this launcher (it belongs to the
//     AP server side, not the game client side). The Settings panel explains this.
//
//   * THE MOD repo is GamingInfinite/BloonsArchipelago (verified live 2026-06-14).
//     It is a C# MelonLoader mod built on top of the BTD6 Mod Helper framework.
//     Latest release: 0.4.3, ships a SINGLE asset "BloonsArchipelago.dll" (~the
//     compiled mod). The mod adds mod settings (URL, port, slot, password + a
//     Connect button) to the in-game mod settings panel opened via the BTD6 Mod
//     Helper menu. It is NOT a standalone exe and NOT a BepInEx mod — it is a
//     MelonLoader mod that requires BTD6 Mod Helper to be installed first.
//
//   * CRITICAL HONESTY — THE MOD DLL IS NOT SELF-SUFFICIENT. BloonsArchipelago.dll
//     requires two things that this launcher cannot bundle:
//       1. MelonLoader (the mod loader) installed into the BTD6 install. The
//          official BTD6 Mod Helper README states that BTD6 Mod Helper installs
//          MelonLoader automatically when the user runs the BTD6ModHelper.exe
//          installer. So the recommended route is the BTD6 Mod Helper installer,
//          not a raw DLL drop.
//       2. BTD6 Mod Helper itself (a MelonLoader mod that provides the mod settings
//          UI framework). It must be present in <BTD6>\Mods\ for BloonsArchipelago
//          to initialize.
//     Therefore the OFFICIAL and RECOMMENDED install route is:
//       a. Install BTD6 Mod Helper (from github.com/gurrenm3/BTD-Mod-Helper) which
//          installs MelonLoader and Mod Helper into the BTD6 installation.
//       b. Place BloonsArchipelago.dll into <BTD6>\Mods\.
//     This plugin CAN download and place BloonsArchipelago.dll into <BTD6>\Mods\
//     (step b), but it CANNOT install MelonLoader or BTD6 Mod Helper (step a) —
//     those require running an installer that touches the game files interactively.
//     The Settings panel leads the user through the guided steps and links.
//
//   * CONNECTION is made IN-GAME via the BTD6 Mod Helper settings panel for
//     BloonsArchipelago (accessible from the in-game mod menu). The mod exposes
//     four settings: URL (default "archipelago.gg"), Port (default 25565), Slot
//     Name (default "Player"), and Password (default blank), plus a Connect button.
//     There is NO command-line arg and NO config file this launcher can pre-write —
//     the mod reads its own MelonLoader settings (verified from the mod source).
//     So this plugin does NOT attempt a connection prefill; the settings panel and
//     a post-launch note surface the session's server/slot so the user can copy
//     them into the in-game mod settings panel.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT Bloons TD 6 via its Steam install (HKCU\Software\Valve\Steam →
//      SteamPath, HKLM\...\WOW6432Node\Valve\Steam → InstallPath), parsing
//      steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\BloonsTD6 via appmanifest_960090.acf. A manual install-dir
//      OVERRIDE (settings folder picker) is supported and takes precedence; it is
//      validated (must contain BloonsTD6.exe) and persisted in this plugin's OWN
//      sidecar (Games/ROMs/btd6/btd6_launcher.json).
//   2. INSTALL/UPDATE (partial, best effort) = download BloonsArchipelago.dll from
//      the latest GitHub release of GamingInfinite/BloonsArchipelago and place it
//      in <BTD6>\Mods\. This is step (b) only — MelonLoader + BTD6 Mod Helper
//      (step a) must be installed separately via the BTD6 Mod Helper installer.
//      The plugin states this honestly and never claims a one-click full install.
//   3. LAUNCH = run BloonsTD6.exe from the detected/override install; if the exe
//      cannot be found but Steam is present, fall back to steam://rungameid/960090.
//      ConnectsItself = true (the mod owns the slot). SupportsStandalone = true
//      (Bloons TD 6 runs fine without the AP mod).
//   4. The Settings panel provides status, guided steps, and links.
//
// ── DEFENSIVE / UNVERIFIED ───────────────────────────────────────────────────
//   * "Installed" = BloonsArchipelago.dll is present anywhere under <BTD6>\Mods\
//     (case-insensitive, recursive). NOT gated on our own stamp, because the user
//     may have placed the DLL manually (we honor that).
//   * MelonLoader detection: <BTD6>\version.dll exists (MelonLoader's doorstop
//     proxy) AND <BTD6>\MelonLoader\ is a directory.
//   * BTD6 Mod Helper detection: <BTD6>\Mods\ contains a file whose name starts
//     with "Btd6ModHelper" or "BTD6ModHelper" (case-insensitive).
//   * Steam library parsing: tolerant hand-written VDF scan; any failure degrades
//     to "Bloons TD 6 not found" rather than throwing.
//
//   * BUILD NOTE: this project sets UseWindowsForms=true alongside UseWPF=true, so
//     WPF UI types are spelled with their FULL namespaces to avoid CS0104 ambiguity,
//     independent of the project's GlobalUsings aliases.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class BloonsTD6Plugin : IGamePlugin
{
    // ── Constants — AP world (GamingInfinite/Archipelago fork) ──────────────────

    /// EXACT AP game string — verified against worlds/bloonstd6/__init__.py:
    /// `game = "Bloons TD6"`. Required by the AP server to identify the world.
    public string ApWorldName => "Bloons TD6";

    /// The apworld release page (GamingInfinite/Archipelago), where the user must
    /// download bloonstd6.apworld and install it into their AP server's lib/worlds/.
    private const string ApWorldReleasesUrl =
        "https://github.com/GamingInfinite/Archipelago/releases";
    private const string ApWorldLatestUrl =
        "https://github.com/GamingInfinite/Archipelago/releases/latest";

    // ── Constants — BloonsArchipelago mod (GamingInfinite/BloonsArchipelago) ────

    private const string ModGhOwner = "GamingInfinite";
    private const string ModGhRepo  = "BloonsArchipelago";

    private const string ModReleasesApiUrl =
        $"https://api.github.com/repos/{ModGhOwner}/{ModGhRepo}/releases";
    private const string ModReleasesPageUrl =
        $"https://github.com/{ModGhOwner}/{ModGhRepo}/releases";

    /// Pinned fallback — verified live 2026-06-14.
    private const string FallbackModVersion = "0.4.3";
    private const string FallbackModDllUrl  =
        $"https://github.com/{ModGhOwner}/{ModGhRepo}/releases/download/" +
        $"{FallbackModVersion}/BloonsArchipelago.dll";

    private const string ModDllName = "BloonsArchipelago.dll";

    // ── Constants — BTD6 Mod Helper (gurrenm3/BTD-Mod-Helper) ───────────────────

    private const string ModHelperUrl = "https://github.com/gurrenm3/BTD-Mod-Helper";
    private const string ModHelperSetupUrl =
        "https://github.com/gurrenm3/BTD-Mod-Helper#readme";

    // ── Constants — Steam (Bloons TD 6, appid 960090) ───────────────────────────

    private const string Btd6SteamAppId      = "960090";
    private const string Btd6SteamCommonName = "BloonsTD6";
    private const string Btd6ExeName         = "BloonsTD6.exe";
    private static readonly string SteamRunUrl = $"steam://rungameid/{Btd6SteamAppId}";

    // ── HTTP client ──────────────────────────────────────────────────────────────

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders =
        {
            { "User-Agent", "Archipelago-Launcher/2.0" },
        },
    };

    // ── IGamePlugin — Identity ───────────────────────────────────────────────────

    public string GameId      => "btd6";
    public string DisplayName => "Bloons TD 6";
    public string Subtitle    => "Native PC · BTD6 Mod Helper + BloonsArchipelago";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "btd6.png");

    public string ThemeAccentColor => "#E42020";   // Bloons red

    public string[] GameBadges => new[] { "Steam · needs mod", "Custom AP World" };

    public string Description =>
        "Bloons TD 6, the tower defense game by Ninja Kiwi, played through " +
        "BloonsArchipelago — a MelonLoader mod (via BTD6 Mod Helper) that is the " +
        "in-game Archipelago client, so the game connects to the multiworld itself " +
        "with no emulator and no Lua bridge. Maps and medals become checks shuffled " +
        "across the multiworld; monkey towers, heroes, and knowledge unlocks are " +
        "received as items. This uses a CUSTOM Archipelago world maintained at " +
        "github.com/GamingInfinite/Archipelago — you need to install the " +
        "bloonstd6.apworld into your Archipelago server's lib/worlds/ folder before " +
        "generating a game. You bring your own copy of Bloons TD 6 (owned on Steam), " +
        "install BTD6 Mod Helper + MelonLoader into the game, then add " +
        "BloonsArchipelago.dll to your Mods folder. You connect from the in-game " +
        "mod settings panel. The launcher detects your BTD6 Steam install and can " +
        "download the mod DLL for you.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means BloonsArchipelago.dll is present under <BTD6>\Mods\.
    /// We do NOT gate on our own stamp — the user may have placed the DLL manually.
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    public bool ConnectsItself    => true;
    public bool SupportsStandalone => true;

    // ── GameDirectory — where downloads / sidecar live ───────────────────────────

    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "BloonsTD6");

    private string RomLibraryDirectory
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);

    private string SettingsSidecarPath
        => Path.Combine(RomLibraryDirectory, "btd6_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────────

    // BloonsArchipelago reports checks and items to the AP server directly.
    // These events exist for interface compatibility (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            InstalledVersion = FindInstalledModDll() != null
                ? (ReadStampedVersion() ?? "installed")
                : null;
        }
        catch { InstalledVersion = null; }

        try
        {
            var (version, _) = await ResolveLatestModAsync(ct);
            AvailableVersion = version;
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 0. We need the BTD6 install to know where to drop the mod DLL.
        progress.Report((2, "Locating your Bloons TD 6 installation..."));
        string? gameDir = ResolveBtd6Dir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a Bloons TD 6 installation. Open this game's " +
                "Settings and pick your Bloons TD 6 folder (the one containing " +
                "\"BloonsTD6.exe\"), or install Bloons TD 6 via Steam first. " +
                "The Archipelago mod is added on top of your own copy of the game.");

        string modsDir = Path.Combine(gameDir, "Mods");

        // 1. Resolve the latest mod release.
        progress.Report((6, "Checking the latest BloonsArchipelago mod release..."));
        var (version, dllUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (dllUrl == null)
            throw new InvalidOperationException(
                "Could not resolve the BloonsArchipelago mod download URL. Check " +
                "your internet connection, or download BloonsArchipelago.dll " +
                "manually from: " + ModReleasesPageUrl +
                " and place it into your <BloonsTD6>\\Mods\\ folder.");

        // 2. Download BloonsArchipelago.dll and place it into <BTD6>\Mods\.
        //    HONEST: this stages only the AP mod DLL. MelonLoader and BTD6 Mod
        //    Helper must already be installed (see the guided steps in Settings).
        progress.Report((10, $"Downloading BloonsArchipelago {version}..."));
        Directory.CreateDirectory(modsDir);
        string destDll = Path.Combine(modsDir, ModDllName);

        using (var response = await _http.GetAsync(
            dllUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            long total      = response.Content.Headers.ContentLength ?? -1;
            long downloaded = 0;

            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(destDll);
            var buf = new byte[81920];
            int bytesRead;
            while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
                downloaded += bytesRead;
                if (total > 0)
                {
                    int pct = (int)(10 + 75 * downloaded / total);
                    progress.Report((pct, $"Downloading... {downloaded / 1000}KB"));
                }
            }
            await dst.FlushAsync(ct);
        }

        WriteStampedVersion(version);
        InstalledVersion = version;

        bool melonLoaderPresent = Directory.Exists(Path.Combine(gameDir, "MelonLoader"))
                                  && File.Exists(Path.Combine(gameDir, "version.dll"));
        bool modHelperPresent   = FindModHelperDll(modsDir) != null;

        progress.Report((100,
            $"BloonsArchipelago {version} installed to {modsDir}. " +
            (melonLoaderPresent && modHelperPresent
                ? "MelonLoader and BTD6 Mod Helper detected — you should be ready. "
                : "IMPORTANT: BloonsArchipelago requires MelonLoader and BTD6 Mod " +
                  "Helper to be installed first. Open this game's Settings for " +
                  "the guided install steps. ") +
            "To play: launch BTD6, open the BTD6 Mod Helper mod menu, find " +
            "BloonsArchipelago, and enter your AP server URL, port, and slot name. " +
            "You also need to install the bloonstd6.apworld on your AP server — see " +
            "Settings for the link to the apworld releases."));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // HONEST: BloonsArchipelago's connection is entered via the in-game BTD6
        // Mod Helper mod settings panel (URL, Port, Slot, Password fields) — there
        // is no command-line or config-file prefill mechanism. ConnectsItself = true:
        // the launcher must NOT hold its own ApClient on this slot while the mod runs.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartBloonsTD6();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartBloonsTD6();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // BloonsArchipelago receives items from the AP server directly.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20),
        };

        string? gameDir       = ResolveBtd6Dir();
        string? overrideDir   = LoadOverrideDir();
        string? modsDir       = gameDir != null ? Path.Combine(gameDir, "Mods") : null;
        string? modDll        = FindInstalledModDll();
        bool    melonLoaded   = gameDir != null
                                && Directory.Exists(Path.Combine(gameDir, "MelonLoader"))
                                && File.Exists(Path.Combine(gameDir, "version.dll"));
        bool    modHelperOk   = modsDir != null && FindModHelperDll(modsDir) != null;

        // ── Honesty header ────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Bloons TD 6 is your own game (Steam) with the BloonsArchipelago " +
                   "mod added on top. The mod requires MelonLoader and BTD6 Mod Helper " +
                   "to be installed first — the recommended route is the BTD6 Mod Helper " +
                   "installer (see guided steps below). The launcher can download " +
                   "BloonsArchipelago.dll for you, but cannot install MelonLoader or " +
                   "BTD6 Mod Helper. This is a custom AP World — you must also install " +
                   "the bloonstd6.apworld on your AP server. Connection is entered " +
                   "in-game via the BTD6 Mod Helper mod menu.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: BTD6 install ─────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "BLOONS TD 6 INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Bloons TD 6 not detected. Pick your install folder below, or " +
              "install Bloons TD 6 via Steam first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = melonLoaded
                ? "MelonLoader detected (version.dll + MelonLoader\\ folder present)."
                : "MelonLoader NOT detected. Install BTD6 Mod Helper (see steps below) " +
                  "to get MelonLoader.",
            FontSize = 11, Foreground = melonLoaded ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modHelperOk
                ? "BTD6 Mod Helper DLL found in Mods\\."
                : "BTD6 Mod Helper NOT found in Mods\\. Required by BloonsArchipelago.",
            FontSize = 11, Foreground = modHelperOk ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                ? "BloonsArchipelago.dll found: " + modDll
                : "BloonsArchipelago.dll NOT found in Mods\\. Use Install on the " +
                  "Play tab to download it, or download it manually (link below).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? gameDir ?? "",
            IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Bloons TD 6 install folder (the one containing " +
                          "\"BloonsTD6.exe\"). Detected from Steam automatically; set " +
                          "it here to override (non-standard Steam library, etc.).",
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
                Title            = "Select your Bloons TD 6 install folder",
                InitialDirectory = Directory.Exists(overrideDir ?? gameDir ?? "")
                                   ? (overrideDir ?? gameDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() != true) return;

            string picked = dlg.FolderName;
            string? bad   = ValidateInstallDir(picked);
            if (bad != null)
            {
                System.Windows.MessageBox.Show(bad, "Not a Bloons TD 6 folder",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }
            // Descend one level if the user picked the Steam "common" parent.
            if (!LooksBtd6Dir(picked))
            {
                string nested = Path.Combine(picked, Btd6SteamCommonName);
                if (LooksBtd6Dir(nested)) picked = nested;
            }
            SaveOverrideDir(picked);
            dirBox.Text = picked;
        };
        System.Windows.Controls.DockPanel.SetDock(dirBtn, System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Steam installs are detected automatically (appid 960090). Use " +
                   "this picker for a non-standard Steam library or a manual install.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Custom AP World ──────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CUSTOM AP WORLD (SERVER SIDE)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Bloons TD 6 uses a CUSTOM Archipelago world (not included in the " +
                   "official Archipelago release). You must download bloonstd6.apworld " +
                   "from the GamingInfinite/Archipelago releases page (link below) and " +
                   "place it into your AP server's lib/worlds/ folder before generating " +
                   "a multiworld game. The apworld is separate from the mod DLL — it " +
                   "lives on the AP server side, not in your BTD6 install.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Connection ───────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in-game via the BTD6 Mod Helper menu)",
            FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Launch BTD6 (with MelonLoader + BTD6 Mod Helper loaded). In the " +
                   "BTD6 Mod Helper mod menu, find BloonsArchipelago. Set these fields: " +
                   "URL (e.g. archipelago.gg), Port (default 25565), Slot Name (your " +
                   "slot name), and Password (leave blank if none). Then click Connect. " +
                   "This launcher cannot pre-fill these values — enter them in-game.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP (full install steps)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Bloons TD 6 (Steam, PC). Install it if you have not. Use the " +
                "\"Select folder...\" picker above if it was not detected automatically.",
            "2. Install BTD6 Mod Helper from the link below (see BTD6 Mod Helper on " +
                "GitHub). Run the BTD6ModHelper.exe installer — it installs MelonLoader " +
                "into your BTD6 folder and adds the Mod Helper DLL to your Mods folder.",
            "3. Click \"Install\" on the Play tab to download BloonsArchipelago.dll into " +
                "your BTD6 Mods folder automatically. Or download it manually from the " +
                "BloonsArchipelago releases link below and place it in <BTD6>\\Mods\\.",
            "4. Install the bloonstd6.apworld on your AP server: download it from the " +
                "GamingInfinite/Archipelago releases page (link below) and place it in " +
                "the server's lib/worlds/ folder. Then generate your multiworld game as " +
                "usual with game: \"Bloons TD6\" in your YAML.",
            "5. Launch BTD6. The BTD6 Mod Helper mod menu should appear. Find " +
                "BloonsArchipelago, set your AP server URL, port, slot name, and password, " +
                "then click Connect. The game connects to the multiworld directly — the " +
                "launcher does not need to stay open once BTD6 is running.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LINKS", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("BTD6 Mod Helper (MelonLoader installer + Mod Helper) ↗",  ModHelperUrl),
            ("BloonsArchipelago mod releases ↗",                         ModReleasesPageUrl),
            ("bloonstd6.apworld releases (GamingInfinite/Archipelago) ↗", ApWorldReleasesUrl),
            ("Archipelago Official ↗",                                   "https://archipelago.gg"),
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

    // ── News feed ─────────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // Pull mod release notes from the GitHub API (newest first).
        try
        {
            string json = await _http.GetStringAsync(ModReleasesApiUrl, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var pub) &&
                    pub.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(pub.GetString(), out date);

                string tag = el.TryGetProperty("tag_name",   out var t) ? t.GetString() ?? "" : "";
                string body = el.TryGetProperty("body",       out var b) ? b.GetString() ?? "" : "";
                string htmlUrl = el.TryGetProperty("html_url", out var h) ? h.GetString() ?? ModReleasesPageUrl : ModReleasesPageUrl;

                items.Add(new NewsItem(
                    Title:   $"BloonsArchipelago {tag}",
                    Body:    string.IsNullOrWhiteSpace(body)
                             ? "See the release page for changes."
                             : body,
                    Version: tag,
                    Date:    date,
                    Url:     htmlUrl
                ));
                if (items.Count >= 8) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — release resolution ──────────────────────────────────────

    /// Resolve the latest BloonsArchipelago release from the GitHub releases API:
    /// version tag + direct URL to BloonsArchipelago.dll. Falls back to the pinned
    /// version when the API is unreachable.
    private async Task<(string Version, string? DllUrl)> ResolveLatestModAsync(
        CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(ModReleasesApiUrl, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) goto fallback;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                string? tag = el.TryGetProperty("tag_name", out var t)
                              ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(tag)) continue;

                if (!el.TryGetProperty("assets", out var assets) ||
                    assets.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var asset in assets.EnumerateArray())
                {
                    string? name = asset.TryGetProperty("name", out var n)
                                   ? n.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    if (name.Equals(ModDllName, StringComparison.OrdinalIgnoreCase))
                    {
                        string? dlUrl = asset.TryGetProperty("browser_download_url", out var dl)
                                        ? dl.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(dlUrl))
                            return (tag!, dlUrl);
                    }
                }
                break; // only the newest release is of interest
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable — pinned fallback */ }

        fallback:
        return (FallbackModVersion, FallbackModDllUrl);
    }

    // ── Private helpers — BTD6 Steam detection ────────────────────────────────────

    private string? ResolveBtd6Dir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksBtd6Dir(ov)) return ov;
        try { return DetectSteamBtd6Dir(); }
        catch { return null; }
    }

    private static bool LooksBtd6Dir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, Btd6ExeName));
        }
        catch { return false; }
    }

    private static string? DetectSteamBtd6Dir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{Btd6SteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksBtd6Dir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, Btd6SteamCommonName);
                    if (LooksBtd6Dir(conventional)) return conventional;
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

        string? hklm2 = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm2)) yield return NormalizeSteamPath(hklm2);

        string? pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(pf86))
            yield return Path.Combine(pf86, "Steam");
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
            yield return text.Substring(open + 1, close - open - 1)
                             .Replace("\\\\", "\\");
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

    // ── Private helpers — mod detection ──────────────────────────────────────────

    /// Find BloonsArchipelago.dll anywhere under <BTD6>\Mods\ (recursive,
    /// case-insensitive). Returns the matched path or null.
    private string? FindInstalledModDll()
    {
        try
        {
            string? game = ResolveBtd6Dir();
            if (game == null) return null;
            string modsDir = Path.Combine(game, "Mods");
            if (!Directory.Exists(modsDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(modsDir, "*.dll", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(dll).Equals(ModDllName, StringComparison.OrdinalIgnoreCase))
                    return dll;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    /// Find any BTD6 Mod Helper DLL in the given Mods directory (a file whose
    /// name starts with "Btd6ModHelper" or "BTD6ModHelper", case-insensitive).
    private static string? FindModHelperDll(string modsDir)
    {
        if (!Directory.Exists(modsDir)) return null;
        try
        {
            foreach (string dll in Directory.EnumerateFiles(modsDir, "*.dll", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileNameWithoutExtension(dll);
                if (name.StartsWith("Btd6ModHelper", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("BTD6ModHelper",  StringComparison.OrdinalIgnoreCase))
                    return dll;
            }
        }
        catch { }
        return null;
    }

    // ── Private helpers — install-dir validation ──────────────────────────────────

    public string? ValidateInstallDir(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Bloons TD 6 install folder.";

        if (LooksBtd6Dir(folder)) return null;

        try
        {
            string nested = Path.Combine(folder, Btd6SteamCommonName);
            if (LooksBtd6Dir(nested)) return null;
        }
        catch { }

        return "That does not look like a Bloons TD 6 installation. Pick the folder " +
               "that contains \"BloonsTD6.exe\" — for Steam this is usually " +
               @"...\steamapps\common\BloonsTD6.";
    }

    // ── Private helpers — launch ──────────────────────────────────────────────────

    private void StartBloonsTD6()
    {
        string? game = ResolveBtd6Dir();
        string? exe  = game != null ? Path.Combine(game, Btd6ExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = game!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Bloons TD 6.");

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

        // Fall back to Steam if the exe is not found but Steam is present.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true; // best-effort; Steam owns the process
                return;
            }
            catch { }
        }

        throw new FileNotFoundException(
            "Could not find \"BloonsTD6.exe\". Open this game's Settings and " +
            "pick your Bloons TD 6 install folder, or install the game via Steam.",
            Btd6ExeName);
    }

    // ── Private helpers — settings sidecar ───────────────────────────────────────

    private sealed class Btd6Settings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private Btd6Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<Btd6Settings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(Btd6Settings s)
    {
        try
        {
            Directory.CreateDirectory(RomLibraryDirectory);
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
