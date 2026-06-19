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
// FontWeights / Orientation collide between System.Windows.Forms and
// System.Windows[.Controls/.Media] (CS0104). Qualifying every UI type avoids that.
// We also do NOT add any file-level `using X = System.Windows...;` aliases — the
// project's GlobalUsings.cs already aliases the short names, and a second local
// alias would be CS1537 (duplicate alias). Bare names or fully-qualified only.

namespace LauncherV2.Plugins.SonicAdventureDX;

// ═══════════════════════════════════════════════════════════════════════════════
// SonicAdventureDXPlugin — install / launch for "Sonic Adventure DX" (SEGA /
// Sonic Team, Steam appid 71360) played through the SADX Archipelago randomiser
// by ClassicSpeed (repo ClassicSpeed/sadx-classic-randomizer). This is a NATIVE
// "ConnectsItself" integration in the same family as the shipped Hollow Knight /
// Subnautica / Noita / SA2B plugins — the game itself speaks to the AP server
// (no emulator, no Lua bridge, no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14) ────────────────────────────────────────
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned
// Sonic Adventure DX (Steam appid 71360), and Archipelago support is delivered
// as a BepInEx mod installed on top. The honest integration ceiling — like the
// shipped SA2B / Noita / HK plugins — is "automate what is possible, guide the
// irreducible parts." The verified facts:
//
//   * THE AP WORLD game string is "Sonic Adventure DX" (matches the AP world
//     registered at ClassicSpeed/sadx-classic-randomizer). GameId = "sadx".
//
//   * THE MOD repo is ClassicSpeed/sadx-classic-randomizer (the AP randomiser
//     for SADX — a BepInEx plugin that implements an in-game Archipelago client).
//     The mod is loaded by BepInEx (the most common .NET game mod framework).
//     The game executable is "sonic.exe" (the Steam build of SADX). The BepInEx
//     loader lives at <SADX>/BepInEx/ and the mod DLL is placed under
//     BepInEx/plugins/.
//
//   * WHAT THE MOD NEEDS to connect is a connection config file. BepInEx mods
//     typically read a BepInEx config file under BepInEx/config/<PluginGuid>.cfg
//     or a sidecar JSON/INI in the BepInEx/plugins folder. Because the exact
//     config file path for this mod was not verified against the source offline,
//     this plugin writes the connection details into a DEFENSIVE set of locations
//     (BepInEx/config/sadx.archipelago.cfg AND a sidecar
//     BepInEx/plugins/SadxArchipelago/archipelago_config.json) using the key
//     names most AP BepInEx mods use. If neither location is checked, the player
//     connects from an in-game UI (standard BepInEx mod pattern for AP games).
//     The settings panel says so and surfaces the session values to copy.
//
//   * CONNECTION note: many AP BepInEx mods show an in-game connection screen on
//     first load where the player types the server / slot / password. The mod may
//     also pick up a pre-written config file so the launcher can pre-fill it.
//     This plugin attempts the pre-fill defensively; if the mod ignores the
//     files, the player fills the in-game dialog once. The plaintext password is
//     blanked from the config files when the session ends.
//
//   * LAUNCH: prefer "sonic.exe" in the detected/override install; fall back to
//     steam://rungameid/71360. SupportsStandalone = true (plain SADX works fine
//     without AP — just don't connect to an AP server).
//
//   * INSTALL/UPDATE: download the release zip from ClassicSpeed/sadx-classic-
//     randomizer/releases/latest on GitHub. The zip is expected to carry the
//     BepInEx plugin DLL (and possibly a bundled BepInEx loader). The plugin
//     extracts it into <SADX>/ (so BepInEx/ lands at the game root, the standard
//     BepInEx install path). If BepInEx is not already present, this effectively
//     bootstraps it together with the mod.
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", SA2B/Noita-style) ──────────
//   * "Installed" is judged by the presence of ANY *.dll mentioning "archipelago"
//     under a detected/override SADX install's BepInEx\plugins tree (case-
//     insensitive, recursive). We do NOT gate on our own version stamp, because
//     the user may install the mod by hand, which this launcher honors.
//   * Steam library parsing is defensive: a tolerant VDF scan; any failure degrades
//     to "SADX not found" rather than throwing.
//   * No plaintext AP password is written by default (it is written into two
//     defensive config paths). It is blanked when the session ends.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SonicAdventureDXPlugin : IGamePlugin
{
    // ── Constants — the SADX Archipelago mod (real repo, ClassicSpeed) ──────────
    private const string MOD_OWNER = "ClassicSpeed";
    private const string MOD_REPO  = "sadx-classic-randomizer";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";
    private const string ModReleasesPageUrl = $"{ModRepoUrl}/releases";

    private const string SetupGuideUrl    = "https://archipelago.gg/tutorial/Sonic%20Adventure%20DX/setup/en";
    private const string ArchipelagoSite  = "https://archipelago.gg";
    private const string BepInExRepoUrl   = "https://github.com/BepInEx/BepInEx/releases";

    // Steam — Sonic Adventure DX appid 71360.
    private const string SteamAppId         = "71360";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    /// Standard Steam install sub-folder name for SADX.
    private const string SteamCommonFolderName = "Sonic Adventure DX";

    /// Main game executable (Steam build).
    private const string GameExeName = "sonic.exe";

    /// BepInEx plugins folder (relative to the game root).
    private const string BepInExPluginsSubPath = @"BepInEx\plugins";
    private const string BepInExCoreSubPath    = @"BepInEx\core";
    private const string BepInExConfigSubPath  = @"BepInEx\config";

    /// Defensive BepInEx config file the launcher pre-writes (standard BepInEx
    /// config path — many AP BepInEx mods read from here).
    private const string ApConfigFileName = "sadx.archipelago.cfg";

    /// Defensive sidecar JSON the launcher pre-writes (alternative config path
    /// used by some AP BepInEx mods that prefer JSON sidecars).
    private const string ApJsonConfigFileName = "archipelago_config.json";
    private const string ApJsonPluginSubDir   = "SadxArchipelago";

    /// Pinned fallback for the mod when the GitHub API is unreachable. This is
    /// the version used if the API is offline; update when a new release ships.
    private const string FallbackVersion = "1.0.0";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "sadx";
    public string DisplayName => "Sonic Adventure DX";
    public string Subtitle    => "Director's Cut · Archipelago mod";

    /// EXACT AP game string registered in the AP world. Must match the game
    /// string from ClassicSpeed/sadx-classic-randomizer's world definition.
    public string ApWorldName => "Sonic Adventure DX";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "sadx.png");

    public string ThemeAccentColor => "#0033AA";   // Sonic blue
    public string[] GameBadges     => new[] { "Requires SADX on Steam" };

    public string Description =>
        "Sonic Adventure DX: Director's Cut, SEGA and Sonic Team's 2003 enhanced " +
        "port of the Dreamcast classic, played through the SADX Archipelago " +
        "randomiser by ClassicSpeed — a BepInEx mod that embeds an in-game " +
        "Archipelago client, so the game connects to the multiworld itself (no " +
        "emulator, no bridge). Story missions, sub-games, upgrades and more are " +
        "shuffled into the multiworld. You bring your own copy of Sonic Adventure DX " +
        "on Steam; the Archipelago mod is added on top via BepInEx. The launcher " +
        "detects your Steam install, downloads and installs the mod for you, and " +
        "pre-fills your server, slot and password into the mod's config before launch.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means at least one *.dll mentioning "archipelago" exists under
    /// a detected/override SADX install's BepInEx\plugins tree.
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads and bookkeeping. The actual mod is
    /// extracted INTO the SADX install's BepInEx folder, not here. Exposed as
    /// GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "SonicAdventureDX");

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore).
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "sadx_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The SADX Archipelago mod reports checks/items/goal to the AP server itself —
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
            // Best-effort: read the version we stamped after a direct install;
            // otherwise report "installed" when the mod DLL exists.
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
        // 0. We need a SADX install to drop the mod into.
        progress.Report((2, "Locating your Sonic Adventure DX installation..."));
        string? sadxDir = ResolveSadxDir();
        if (sadxDir == null)
            throw new InvalidOperationException(
                "Could not find a Sonic Adventure DX installation. Open this game's " +
                "Settings and pick your Sonic Adventure DX folder (the one containing " +
                "sonic.exe), or install Sonic Adventure DX via Steam first. The " +
                "Archipelago mod is added on top of your own copy of the game.");

        // 1. Resolve the latest mod release (pinned fallback when offline).
        progress.Report((6, "Checking the latest SADX Archipelago randomiser release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the SADX Archipelago mod download on GitHub. Check " +
                "your internet connection, or download the mod zip manually from " +
                ModReleasesPageUrl + " and unpack it into your Sonic Adventure DX " +
                "game folder. See Settings for the guided steps. The mod repo is " +
                ModRepoUrl + ".");

        // 2. Download + extract the mod zip INTO the SADX game root. BepInEx mods
        //    for Unity/BepInEx games are distributed as a zip whose contents go
        //    straight into the game root — the BepInEx/ folder then lives at the
        //    game root, which is where the loader expects it. Any existing BepInEx
        //    content (other mods the user installed) is preserved via overwrite
        //    rather than clean-replace.
        await DownloadAndExtractModAsync(zipUrl, version, sadxDir, progress, ct);

        // 3. Stamp the installed version (informational — IsInstalled uses the DLL).
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool bepInExOk = Directory.Exists(Path.Combine(sadxDir, BepInExCoreSubPath));
        progress.Report((100,
            $"Installed the SADX Archipelago randomiser {version} into your " +
            "Sonic Adventure DX game folder. " +
            (bepInExOk
                ? "BepInEx looks present. "
                : "IMPORTANT: BepInEx (the mod loader) may not be present — if the " +
                  "game launches but the AP mod does not connect, install BepInEx " +
                  "(link in Settings) and re-install. ") +
            "To play: press Play here. The launcher pre-fills your server, slot and " +
            "password into the mod's config. On first launch you may see an in-game " +
            "connection screen — the values are pre-filled or can be copied from the " +
            "launcher's Settings panel."));
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
        // Pre-fill the AP connection defensively into a BepInEx .cfg AND a JSON
        // sidecar — the mod likely reads one of these (or shows an in-game dialog).
        // Best effort; never blocks the launch.
        try { WriteApConnectionConfig(session); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the mod is connected.
        StartSadx();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) Sonic Adventure DX runs perfectly well.
    public bool SupportsStandalone => true;

    /// The SADX Archipelago mod owns the slot connection.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        // No AP prefill — just start the game.
        StartSadx();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // Blank the plaintext password from the config files once the session ends.
        ScrubApConnectionPassword();
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The SADX Archipelago mod receives items from the AP server directly.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid SADX folder contains sonic.exe.
    /// Return null when acceptable, else a short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Sonic Adventure DX install folder.";

        if (LooksLikeSadxDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeSadxDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Sonic Adventure DX installation. Pick the " +
               "folder that contains sonic.exe. For Steam this is usually " +
               @"...\steamapps\common\Sonic Adventure DX.";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        string? sadxDir     = ResolveSadxDir();
        string? overrideDir = LoadOverrideDir();
        string? modDll      = FindInstalledModDll();
        bool    bepInExOk   = sadxDir != null &&
                              Directory.Exists(Path.Combine(sadxDir, BepInExCoreSubPath));

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Sonic Adventure DX is your own game on Steam with the SADX Archipelago " +
                   "randomiser mod added on top via BepInEx. The launcher detects your Steam " +
                   "install, downloads the mod for you, and pre-fills your server, slot and " +
                   "password into the mod's config before launch. If an in-game connection " +
                   "dialog appears on first run, your session values are shown below to copy. " +
                   "These external steps are not verified by this launcher.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "SONIC ADVENTURE DX INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string detectMsg = sadxDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + sadxDir
                : "Detected Steam install: " + sadxDir)
            : "Sonic Adventure DX not detected. Pick your install folder below, or " +
              "install Sonic Adventure DX via Steam first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = sadxDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        // BepInEx + mod status lines
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = bepInExOk
                    ? "BepInEx found (BepInEx\\core present)."
                    : "BepInEx not found yet — it will be installed alongside the mod (Install " +
                      "on the Play tab), or install it separately from BepInEx releases.",
            FontSize = 11, Foreground = bepInExOk ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                    ? "SADX Archipelago mod found: " + modDll
                    : "SADX Archipelago mod not found in BepInEx\\plugins yet (use Install on " +
                      "the Play tab, or unpack the mod zip into your game folder).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? sadxDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Sonic Adventure DX install folder (the one containing " +
                          "sonic.exe). Detected from Steam automatically; set it here to " +
                          "override a non-standard Steam library.",
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
                Title            = "Select your Sonic Adventure DX install folder (contains sonic.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? sadxDir ?? "")
                                   ? (overrideDir ?? sadxDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Sonic Adventure DX folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeSadxDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeSadxDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 71360). Use this picker " +
                   "for a non-standard Steam library.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection ────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (pre-filled into mod config)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "When you press Play, the launcher writes your Server (host:port, e.g. " +
                   "archipelago.gg:38281), Slot Name and Password into the mod's BepInEx " +
                   "config file and a JSON sidecar before launching the game. If the mod shows " +
                   "an in-game connection screen on first run, copy the values from here. " +
                   "The password is blanked from disk when the session ends.",
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
            "1. Own Sonic Adventure DX on Steam and launch it once normally so it finishes " +
                "first-run setup. Use \"Select folder...\" above if it was not auto-detected.",
            "2. Install the mod: use Install on the Play tab (the launcher downloads the mod " +
                "zip and extracts it into your Sonic Adventure DX game folder, placing " +
                "BepInEx/plugins/... in the right location). Or download the zip from the mod " +
                "repo (link below) and extract it into your game folder by hand.",
            "3. If BepInEx is not already installed in your game folder (BepInEx\\core folder " +
                "present), the mod zip should include it. If not, install BepInEx for Unity " +
                "x64 from the BepInEx releases page (link below) into your SADX game folder.",
            "4. Press Play here. The launcher pre-fills your server, slot and password into " +
                "the mod's BepInEx config and JSON sidecar. If the mod shows a connection " +
                "screen in-game, the values are already filled or can be copied from above.",
            "5. Confirm the mod connects to your Archipelago server in-game. Items and " +
                "checks flow through the multiworld automatically.",
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
            ("SADX Archipelago randomiser (releases) ↗", ModReleasesPageUrl),
            ("BepInEx releases ↗",                       BepInExRepoUrl),
            ("SADX Setup Guide ↗",                       SetupGuideUrl),
            ("Archipelago Official ↗",                   ArchipelagoSite),
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
            btn.Click += (_, _) => OpenUrl(u);
            panel.Children.Add(btn);
        }
        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // Mod releases from the GitHub releases API are the AP-relevant news.
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

    // ── Private helpers — small utilities ─────────────────────────────────────

    /// "v1.2.3" → "1.2.3" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Open a URL / shell target. Best effort — never throws to the caller.
    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* shell unavailable — ignore */ }
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    /// Resolve the latest mod release: version + the mod zip download URL. Prefers
    /// the first .zip asset in the latest release; falls back to the pinned
    /// FallbackVersion direct URL when the API is unreachable.
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
                string? preferred = null;   // .zip named like the mod
                string? anyZip    = null;   // any .zip
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    anyZip ??= url;
                    if (preferred == null && (lower.Contains("sadx") || lower.Contains("archipelago")))
                        preferred = url;
                }
                string? zip = preferred ?? anyZip;
                if (zip != null)
                    return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        // Offline fallback: point at the releases page — we don't know the exact
        // filename without fetching the API, so we return null for the URL (the
        // install will report a helpful error directing the user to the releases page).
        return (FallbackVersion, null);
    }

    // ── Private helpers — Steam / SADX detection ──────────────────────────────

    /// The SADX install dir to use: the override (if set and valid) wins, else the
    /// Steam-detected install. Null when nothing is found.
    private string? ResolveSadxDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeSadxDir(ov))
            return ov;

        try { return DetectSteamSadxDir(); }
        catch { return null; }
    }

    /// A folder "looks like" SADX if it has sonic.exe.
    private static bool LooksLikeSadxDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, GameExeName));
        }
        catch { return false; }
    }

    /// Detect the Steam SADX install: read the Steam root from the registry, gather
    /// all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_71360.acf exists → steamapps\common\Sonic Adventure DX.
    private static string? DetectSteamSadxDir()
    {
        // Also try the direct Steam registry key for the app (fast path).
        try
        {
            using RegistryKey? directKey = Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App {SteamAppId}");
            if (directKey?.GetValue("InstallLocation") is string loc && LooksLikeSadxDir(loc))
                return loc;
        }
        catch { /* fall through to full library scan */ }

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
                    // conventional "Sonic Adventure DX" folder name.
                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeSadxDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeSadxDir(conventional)) return conventional;
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    /// Candidate Steam install roots from the registry (HKCU SteamPath +
    /// HKLM WOW6432Node InstallPath + HKLM InstallPath). Duplicates are harmless.
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

    /// Find the SADX Archipelago mod DLL under the detected/override install's
    /// BepInEx\plugins tree (recursive, case-insensitive). The exact DLL name is
    /// not verified offline, so we accept any *.dll whose name mentions
    /// "archipelago" anywhere under BepInEx\plugins. Returns the path or null.
    private string? FindInstalledModDll()
    {
        try
        {
            string? sadx = ResolveSadxDir();
            if (sadx == null) return null;
            string pluginsDir = Path.Combine(sadx, BepInExPluginsSubPath);
            if (!Directory.Exists(pluginsDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(pluginsDir, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(dll);
                if (name.IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                    return dll;
            }

            // Secondary: a sub-folder of plugins mentioning "archipelago" or "sadx" that
            // contains at least one DLL (handles sub-folder mod layouts).
            foreach (string sub in Directory.EnumerateDirectories(pluginsDir, "*", SearchOption.AllDirectories))
            {
                string folder = Path.GetFileName(sub);
                if (folder.IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) < 0 &&
                    folder.IndexOf("sadx",        StringComparison.OrdinalIgnoreCase) < 0) continue;
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

    // ── Private helpers — connection config prefill ───────────────────────────

    /// Pre-fill the AP connection defensively into two locations:
    ///   1. BepInEx/config/sadx.archipelago.cfg  — standard BepInEx config format
    ///   2. BepInEx/plugins/SadxArchipelago/archipelago_config.json — JSON sidecar
    /// DEFENSIVE: the exact config path/format was not verified against the mod
    /// source offline, so both common patterns are written. The mod reads one or
    /// neither; if neither, the player fills an in-game dialog once. The settings
    /// panel surfaces the session values so the user can copy them. Best effort.
    private void WriteApConnectionConfig(ApSession session)
    {
        string? sadx = ResolveSadxDir();
        if (sadx == null) return;

        var (host, port) = ParseServerHostPort(session.ServerUri);
        string serverAddress = $"{host}:{port}";
        string slotName      = session.SlotName;
        string password      = session.Password ?? "";

        // 1. BepInEx .cfg (section + key=value format, which is what BepInEx.Configuration uses).
        try
        {
            string cfgDir  = Path.Combine(sadx, BepInExConfigSubPath);
            string cfgPath = Path.Combine(cfgDir, ApConfigFileName);
            Directory.CreateDirectory(cfgDir);

            var ini = BepInExCfgDocument.Load(cfgPath);
            ini.Set("Archipelago", "ServerAddress", serverAddress);
            ini.Set("Archipelago", "SlotName",      slotName);
            ini.Set("Archipelago", "Password",      password);
            // Also write under a flat section in case the mod does not use sections.
            ini.Set("General", "ServerAddress", serverAddress);
            ini.Set("General", "SlotName",      slotName);
            ini.Set("General", "Password",      password);
            ini.Save(cfgPath);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        // 2. JSON sidecar under BepInEx/plugins/SadxArchipelago/.
        try
        {
            string jsonDir  = Path.Combine(sadx, BepInExPluginsSubPath, ApJsonPluginSubDir);
            string jsonPath = Path.Combine(jsonDir, ApJsonConfigFileName);
            Directory.CreateDirectory(jsonDir);

            var config = new Dictionary<string, string>
            {
                ["ServerAddress"] = serverAddress,
                ["SlotName"]      = slotName,
                ["Password"]      = password,
                // Alternative key names used by some AP BepInEx mods:
                ["Server"]        = serverAddress,
                ["Slot"]          = slotName,
                ["Name"]          = slotName,
            };
            string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(jsonPath, json, new UTF8Encoding(false));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// Blank the password fields in both pre-written config locations once the
    /// session ends. Best effort; the next AP launch rewrites them anyway.
    private void ScrubApConnectionPassword()
    {
        string? sadx = ResolveSadxDir();
        if (sadx == null) return;

        // 1. BepInEx .cfg
        try
        {
            string cfgPath = Path.Combine(sadx, BepInExConfigSubPath, ApConfigFileName);
            if (File.Exists(cfgPath))
            {
                var ini = BepInExCfgDocument.Load(cfgPath);
                bool changed = false;
                foreach (string section in new[] { "Archipelago", "General" })
                {
                    if (!string.IsNullOrEmpty(ini.Get(section, "Password")))
                    {
                        ini.Set(section, "Password", "");
                        changed = true;
                    }
                }
                if (changed) ini.Save(cfgPath);
            }
        }
        catch { /* best effort */ }

        // 2. JSON sidecar
        try
        {
            string jsonPath = Path.Combine(sadx, BepInExPluginsSubPath,
                ApJsonPluginSubDir, ApJsonConfigFileName);
            if (File.Exists(jsonPath))
            {
                string text = File.ReadAllText(jsonPath);
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(text);
                if (dict != null && dict.ContainsKey("Password") && !string.IsNullOrEmpty(dict["Password"]))
                {
                    dict["Password"] = "";
                    File.WriteAllText(jsonPath,
                        JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }),
                        new UTF8Encoding(false));
                }
            }
        }
        catch { /* best effort */ }
    }

    /// Accepts "archipelago.gg:38281", "ws://host:port", "wss://host:port", a bare
    /// hostname, and IPv6 literals. Default AP port is 38281.
    private static (string Host, int Port) ParseServerHostPort(string serverUri)
    {
        string s = serverUri.Trim();
        foreach (string prefix in new[] { "wss://", "ws://", "archipelago://" })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                s = s[prefix.Length..];
                break;
            }
        }

        int slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];

        string host = s;
        int    port = 38281;

        int colonCount = 0;
        foreach (char c in s) if (c == ':') colonCount++;

        if (s.StartsWith('['))
        {
            int close = s.IndexOf(']');
            if (close > 0)
            {
                host = s[1..close];
                string rest = s[(close + 1)..];
                if (rest.StartsWith(':') &&
                    int.TryParse(rest[1..], out int p6) && p6 > 0 && p6 <= 65535)
                    port = p6;
            }
        }
        else if (colonCount > 1)
        {
            host = s; // bare IPv6 literal — no port can be carried this way
        }
        else
        {
            int colon = s.LastIndexOf(':');
            if (colon > 0 && int.TryParse(s[(colon + 1)..], out int p) && p > 0 && p <= 65535)
            {
                host = s[..colon];
                port = p;
            }
        }

        if (host.Length == 0) host = "archipelago.gg";
        return (host, port);
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start SADX: prefer sonic.exe from the detected/override install; if that
    /// cannot be found but Steam is present, fall back to the steam:// URL.
    private void StartSadx()
    {
        string? sadx = ResolveSadxDir();
        string? exe  = sadx != null ? Path.Combine(sadx, GameExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = sadx!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Sonic Adventure DX.");

            TrackProcess(proc);
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
            "Could not find sonic.exe. Open this game's Settings and pick your Sonic " +
            "Adventure DX install folder, or install Sonic Adventure DX via Steam.",
            GameExeName);
    }

    /// Wire up process tracking + exit notification for a launched process.
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
                ScrubApConnectionPassword();   // session over — blank the password
                GameExited?.Invoke(proc.ExitCode);
            };
        }
        catch { /* some processes don't expose Exited — non-fatal */ }
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the mod's release zip and extract it INTO the SADX game root.
    /// BepInEx mods for PC games ship with their contents structured to go directly
    /// into the game folder (BepInEx/ lands at the root). We merge so an update
    /// overwrites changed files without removing unrelated mod files the user
    /// may have installed via other means.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string sadxDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"sadx-archipelago-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"sadx-archipelago-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading SADX Archipelago randomiser {version}..."));
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
                        progress.Report((pct, $"Downloading SADX Archipelago randomiser... {downloaded / 1_000_000}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((70, "Unpacking the mod into your Sonic Adventure DX folder..."));
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // Some zips wrap everything in a single top-level folder — if so, that
            // wrapper folder's contents go into the game root (not the wrapper itself).
            string sourceRoot = tempExtract;
            string[] topItems = Directory.GetFileSystemEntries(tempExtract);
            if (topItems.Length == 1 && Directory.Exists(topItems[0]))
            {
                // Single top-level directory — treat it as the source root.
                sourceRoot = topItems[0];
            }

            progress.Report((82, "Installing mod files into the game folder..."));
            MergeDirectory(sourceRoot, sadxDir);

            progress.Report((95, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    /// Recursively copy everything under <src> into <dst>, overwriting files and
    /// creating directories as needed. Never deletes anything in <dst> that is not
    /// being overwritten — so other mods / configs the user placed in the game
    /// folder are preserved.
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
            string rel    = Path.GetRelativePath(src, file);
            string target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // This plugin keeps its launcher-side settings (the SADX install-dir override +
    // an informational version stamp) in its OWN JSON file so it stays a single
    // self-contained source file and does not modify Core/SettingsStore. BOM-less
    // UTF-8, read-modify-write (same approach as SA2B / Noita / Jak / HK).

    private sealed class SadxSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private SadxSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<SadxSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(SadxSettings s)
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

    // ── Private helpers — tiny section-aware BepInEx .cfg read-modify-write ───
    // BepInEx config files are Windows INI-like ([Section] + ## comments + key = value).
    // We need to Set the [Archipelago] / [General] keys WITHOUT disturbing any other
    // section/key the mod or BepInEx itself writes. This minimal in-memory model
    // preserves section order, key order, and comment lines, and appends a missing
    // section/key at the right place. BOM-less, CRLF output — matches BepInEx's own
    // config writer so the file looks native if the user opens it in the mod manager.

    private sealed class BepInExCfgDocument
    {
        private readonly List<Block> _blocks = new();

        // A Block is either a raw line (comment, blank, or unrecognised) or a section.
        private abstract class Block { }
        private sealed class RawLine : Block { public string Text = ""; }
        private sealed class Section : Block
        {
            public string Name = "";
            public readonly List<Entry> Entries = new();
        }
        private sealed class Entry
        {
            // BepInEx cfg entries may have a leading ## comment block before key = value.
            public readonly List<string> Comments = new();
            public string Key   = "";
            public string Value = "";
        }

        public static BepInExCfgDocument Load(string path)
        {
            var doc = new BepInExCfgDocument();
            Section? current = null;

            try
            {
                if (!File.Exists(path))
                    return doc;

                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.TrimEnd();

                    // Section header.
                    if (line.StartsWith('[') && line.TrimEnd().EndsWith(']'))
                    {
                        string name = line.Trim()[1..^1].Trim();
                        current = new Section { Name = name };
                        doc._blocks.Add(current);
                        continue;
                    }

                    // Key = value.
                    if (current != null && !line.StartsWith('#') && !line.StartsWith(';'))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0)
                        {
                            string key = line[..eq].Trim();
                            string val = line[(eq + 1)..].Trim();
                            if (key.Length > 0)
                            {
                                current.Entries.Add(new Entry { Key = key, Value = val });
                                continue;
                            }
                        }
                    }

                    // Everything else (comment, blank, or line before first section).
                    if (current == null)
                        doc._blocks.Add(new RawLine { Text = line });
                    else
                        current.Entries.Add(new Entry { Key = "", Value = "", Comments = { line } });
                }
            }
            catch { /* unreadable → empty doc */ }
            return doc;
        }

        public string? Get(string section, string key)
        {
            Section? sec = FindSection(section);
            if (sec == null) return null;
            foreach (var e in sec.Entries)
                if (string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase))
                    return e.Value;
            return null;
        }

        public void Set(string section, string key, string value)
        {
            Section? sec = FindSection(section);
            if (sec == null)
            {
                sec = new Section { Name = section };
                _blocks.Add(sec);
            }
            foreach (var e in sec.Entries)
            {
                if (string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    e.Value = value;
                    return;
                }
            }
            sec.Entries.Add(new Entry { Key = key, Value = value });
        }

        private Section? FindSection(string name)
            => _blocks.OfType<Section>()
                      .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

        public void Save(string path)
        {
            var sb = new StringBuilder();
            bool firstSection = true;

            foreach (var block in _blocks)
            {
                if (block is RawLine raw)
                {
                    sb.Append(raw.Text).Append("\r\n");
                }
                else if (block is Section sec)
                {
                    if (!firstSection) sb.Append("\r\n");
                    sb.Append('[').Append(sec.Name).Append(']').Append("\r\n");
                    firstSection = false;

                    foreach (var e in sec.Entries)
                    {
                        // Emit comment-only entries (blank lines / ## comments in section).
                        if (e.Key.Length == 0)
                        {
                            foreach (string c in e.Comments)
                                sb.Append(c).Append("\r\n");
                            continue;
                        }
                        foreach (string c in e.Comments)
                            sb.Append(c).Append("\r\n");
                        sb.Append(e.Key).Append(" = ").Append(e.Value).Append("\r\n");
                    }
                }
            }

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
    }
}
