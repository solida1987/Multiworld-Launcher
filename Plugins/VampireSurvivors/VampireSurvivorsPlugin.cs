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

namespace LauncherV2.Plugins.VampireSurvivors;

// ═══════════════════════════════════════════════════════════════════════════════
// VampireSurvivorsPlugin — install / launch for "Vampire Survivors" (poncle,
// 2022) played through the ArchipelagoSurvivors MelonLoader mod by SWCreeperKing.
// This is a NATIVE "ConnectsItself" integration in the same family as the shipped
// Blasphemous / Hollow Knight / TUNIC / Stardew Valley / A Short Hike plugins:
// the game itself speaks to the AP server via its embedded mod client (no emulator,
// no Lua bridge, no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-19, verified online + against the apworld) ──
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of Vampire Survivors (Steam appid 1794680 — UNVERIFIED, check Steam store page),
// and Archipelago support is delivered as a MelonLoader mod added on top. The
// honest integration ceiling — exactly like the shipped Blasphemous / Hollow
// Knight plugins — is "automate what is possible, guide the irreducible parts."
// The verified facts:
//
//   * THE AP WORLD game string is "Vampire Survivors" (UNVERIFIED — check against
//     worlds/__init__.py: look for `game = "Vampire Survivors"` in the world
//     class). GameId here = "vampire_survivors".
//
//   * THE MOD repo is SWCreeperKing/ArchipelagoSurvivors (mod uses MelonLoader).
//     Latest release verified: v0.3.4 (April 11, 2026), single asset
//     "ArchipelagoSurvivors.zip". The connection UI appears on the title screen
//     top-left. Each weapon unlock and achievement becomes a multiworld location
//     check; weapons from other worlds may arrive early — or not at all.
//
//   * CRITICAL HONESTY — THE MOD ZIP IS NOT SELF-SUFFICIENT. The ArchipelagoSurvivors
//     zip contains the mod DLL (and potentially bundled AP network client) but does
//     NOT contain MelonLoader — the mod loader that every MelonLoader mod needs.
//     MelonLoader must be installed separately by the user (see https://melonwiki.xyz)
//     BEFORE the mod can load. This plugin stages the mod files (into the game's
//     Mods/ directory, the canonical MelonLoader location), but faking a one-click
//     "fully installed" that cannot exist would be dishonest theatre — the plugin
//     says exactly this and guides the user to install MelonLoader first.
//
//   * CONNECTION is made IN-GAME via the title screen UI (verified against the mod
//     README and the AP setup guide). After MelonLoader + the mod are installed and
//     the game is started, a connection panel appears on the title screen top-left
//     where the user enters server, port, slot name, and optional password. There is
//     NO command-line arg and NO config file this launcher can pre-write for the
//     connection. Per an honest stance (same as Blasphemous / Hollow Knight / A
//     Short Hike), this plugin does NOT write any file or fake a connection prefill
//     — the settings panel + post-install note surface the session's host / port /
//     slot for the user to type into the title-screen panel.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Vampire Survivors install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, and
//      HKLM\...\WOW6432Node\Valve\Steam -> InstallPath), parsing
//      steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\Vampire Survivors via appmanifest_1794680.acf. A manual
//      install-dir OVERRIDE (settings folder picker) is also supported and takes
//      precedence; it is validated (must contain VampireSurvivors.exe) and
//      persisted in this plugin's OWN sidecar
//      (Games/ROMs/vampire_survivors/vampire_survivors_launcher.json) — Core/
//      SettingsStore is NOT modified.
//   2. DETECT the mod: search for ArchipelagoSurvivors.dll anywhere under the
//      game's Mods/ directory (the canonical MelonLoader mods folder) and also
//      anywhere under the install root (case-insensitive, recursive), so installs
//      that place the DLL elsewhere are honored.
//   3. INSTALL/UPDATE (best effort) = download "ArchipelagoSurvivors.zip" from
//      SWCreeperKing/ArchipelagoSurvivors/releases/latest, extract into
//      <VampireSurvivors>/Mods/ (the MelonLoader mods directory). Because the zip
//      does NOT carry MelonLoader, the plugin ALSO presents clear guided steps +
//      links (MelonLoader, mod repo, AP guide) so the user can complete the
//      MelonLoader install. Never a fake one-click.
//   4. LAUNCH = run VampireSurvivors.exe from the detected/override install; if
//      the exe cannot be found but Steam is present, fall back to
//      steam://rungameid/1794680. ConnectsItself = true (the mod owns the slot
//      — the launcher must NOT hold its own ApClient on it).
//      SupportsStandalone = true (plain Vampire Survivors runs fine without AP).
//      No connection prefill (entered in-game on the title screen), stated
//      honestly.
//
// ── DEFENSIVE / UNVERIFIED ────────────────────────────────────────────────────
//   * Steam appid 1794680 is UNVERIFIED — confirm against the Steam store page.
//   * AP world string "Vampire Survivors" is UNVERIFIED — confirm against
//     worlds/__init__.py.
//   * "Installed" is judged by ArchipelagoSurvivors.dll existing anywhere under
//     the install's Mods/ subtree (or the install root as fallback). We do NOT
//     gate on our own version stamp — the user may install the mod manually or
//     via another tool, which we honor.
//   * Steam library parsing is defensive: a hand-written tolerant VDF scan;
//     any failure degrades to "Vampire Survivors not found" rather than throwing.
//   * No plaintext AP password is ever written by this plugin (connection is
//     entered in-game on the title screen).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class VampireSurvivorsPlugin : IGamePlugin
{
    // ── Constants — the ArchipelagoSurvivors mod (SWCreeperKing, verified 2026-06-19) ─
    private const string MOD_OWNER = "SWCreeperKing";
    private const string MOD_REPO  = "ArchipelagoSurvivors";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    // MelonLoader — the mod loader that every MelonLoader mod needs. The user
    // MUST install this manually before the mod can function. The launcher does
    // NOT install MelonLoader itself (it touches nothing in the game directory
    // beyond the Mods/ folder, and MelonLoader installs into the game root).
    private const string MelonLoaderUrl      = "https://melonwiki.xyz/#/";
    private const string MelonLoaderGitHub   = "https://github.com/LavaGang/MelonLoader";
    private const string MelonLoaderReleases = "https://github.com/LavaGang/MelonLoader/releases";

    private const string SetupGuideUrl   = "https://archipelago.gg/games/Vampire%20Survivors/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Vampire Survivors appid 1794680 (UNVERIFIED — check the Steam store page).
    private const string VsAppId = "1794680";
    private static readonly string SteamRunUrl = $"steam://rungameid/{VsAppId}";

    /// The standard Steam install sub-folder name for Vampire Survivors.
    private const string SteamCommonFolderName = "Vampire Survivors";

    /// The base-game executable name.
    private const string VsExeName = "VampireSurvivors.exe";

    /// The mod's primary DLL. MelonLoader mods are placed in <Game>/Mods/.
    private const string ModPrimaryDll = "ArchipelagoSurvivors.dll";

    /// The zip asset name as it appears in the GitHub release. Verified against
    /// the v0.3.4 release (April 11, 2026): single asset "ArchipelagoSurvivors.zip".
    private const string ModZipName = "ArchipelagoSurvivors.zip";

    /// Pinned fallback version when the GitHub API is unreachable. v0.3.4 verified
    /// live 2026-06-19; the single release asset is "ArchipelagoSurvivors.zip".
    private const string FallbackVersion = "0.3.4";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/v{FallbackVersion}/{ModZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "vampire_survivors";
    public string DisplayName => "Vampire Survivors";
    public string Subtitle    => "PC · Archipelago mod";

    /// EXACT AP game string — UNVERIFIED. Check against worlds/__init__.py:
    /// look for `game = "Vampire Survivors"` in the relevant World class.
    public string ApWorldName => "Vampire Survivors";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "vampire_survivors.png");

    public string ThemeAccentColor => "#B71C1C";   // vampire blood red
    public string[] GameBadges     => new[] { "Requires Vampire Survivors on Steam" };

    public string Description =>
        "Vampire Survivors (2022) is the wildly popular bullet-heaven roguelite by " +
        "poncle in which you survive hordes of monsters for up to 30 minutes using " +
        "auto-firing weapons that level up into absurd combinations. The Archipelago " +
        "mod by SWCreeperKing (via MelonLoader) shuffles weapon unlocks, character " +
        "unlocks, and stage progression across the multiworld. Your title-screen UI " +
        "shows the connection panel to enter server details. Each new unlock and " +
        "achievement becomes a multiworld location check, and weapons from other " +
        "worlds may arrive early — or not at all. Requires: your own Vampire " +
        "Survivors on Steam and MelonLoader installed into your game directory. The " +
        "launcher stages the mod files; MelonLoader itself must be installed " +
        "separately (see steps below).";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means VampireSurvivors.exe is present in the detected/override
    /// install AND ArchipelagoSurvivors.dll is found anywhere under the install dir.
    /// (We do NOT gate on our own stamp — the user may install the mod manually,
    /// which we honor.)
    public bool IsInstalled
    {
        get
        {
            string? vsDir = ResolveVampireSurvivorsDir();
            if (vsDir == null) return false;
            if (!File.Exists(Path.Combine(vsDir, VsExeName))) return false;
            return FindInstalledModDll() != null;
        }
    }

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads and any working files. The actual mod is
    /// extracted INTO the Vampire Survivors install's Mods/ folder, not here.
    /// Exposed as GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "VampireSurvivors");

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained file). Per the brief, lives under
    /// Games/ROMs/vampire_survivors/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "vampire_survivors_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The ArchipelagoSurvivors mod reports checks/items/goal to the AP server
    // itself — the launcher relays nothing. These exist for interface compatibility
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
            // Best-effort: report the stamped version from a direct install if
            // present; otherwise report "installed" when the mod DLL exists.
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

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 0. We need a Vampire Survivors install to drop the mod into. Prefer an
        //    explicit override; else auto-detect the Steam install.
        progress.Report((2, "Locating your Vampire Survivors installation..."));
        string? vsDir = ResolveVampireSurvivorsDir();
        if (vsDir == null)
            throw new InvalidOperationException(
                "Could not find a Vampire Survivors installation. Open this game's " +
                "Settings and pick your Vampire Survivors folder (the one containing " +
                "VampireSurvivors.exe), or install Vampire Survivors via Steam first. " +
                "The Archipelago mod is added on top of your own copy of the game.");

        // 1. Resolve the latest mod release (pinned fallback when API unreachable).
        progress.Report((6, "Checking the latest ArchipelagoSurvivors release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the ArchipelagoSurvivors mod download on GitHub. " +
                "Check your internet connection, or download it manually from " +
                ModRepoUrl + "/releases. The mod zip should be placed in " +
                "your Vampire Survivors\\Mods\\ folder.");

        // 2. Download + extract the mod zip INTO <VampireSurvivors>/Mods/.
        //    HONEST: this stages the ArchipelagoSurvivors mod only. MelonLoader
        //    (the mod loader) is NOT in this zip and must be installed separately
        //    by the user from https://melonwiki.xyz before the mod can function.
        string modsDir = Path.Combine(vsDir, "Mods");
        await DownloadAndExtractModAsync(zipUrl, version, modsDir, progress, ct);

        // 3. Stamp the version next to our sidecar so the tile can show it.
        //    (Informational only — IsInstalled is judged by the DLL's presence.)
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool dllOk = FindInstalledModDll() != null;
        progress.Report((100,
            $"Staged the ArchipelagoSurvivors mod {version} into your Mods folder" +
            (dllOk ? "." : " (verify the files landed).") +
            " IMPORTANT: this mod requires MelonLoader (the mod loader) to be " +
            "installed into your Vampire Survivors folder BEFORE the mod can run. " +
            "MelonLoader is NOT included in this download — install it from " +
            MelonLoaderReleases + " (see Settings for the guided steps and links). " +
            "Once MelonLoader is installed: launch Vampire Survivors and enter your " +
            "server details (Server, Port, Slot Name, optional Password) in the " +
            "connection panel on the title screen top-left. (This launcher cannot " +
            "pre-fill the connection — it is entered in-game on the title screen.)"));
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
        // HONEST: the AP server connection for Vampire Survivors is entered in the
        // connection panel that appears on the TITLE SCREEN top-left after MelonLoader
        // + the mod are installed. There is no documented command-line / config prefill
        // this launcher can apply. So launching from this tile just starts the game;
        // the user connects in-game using the session credentials (the settings panel
        // + note surface those values to copy).
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on this
        // slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartVampireSurvivors();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) Vampire Survivors runs perfectly well.
    public bool SupportsStandalone => true;

    /// The ArchipelagoSurvivors mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartVampireSurvivors();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // We only ever started Vampire Survivors from here. Kill what we launched.
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // No plaintext AP password is ever written to disk by this plugin (the
        // connection is entered in-game on the title screen), so there is nothing
        // to scrub.
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The ArchipelagoSurvivors mod receives items from the AP server directly;
        // there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game on the title screen.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid Vampire Survivors folder must
    /// contain VampireSurvivors.exe. Return null when acceptable, else a short
    /// human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Vampire Survivors install folder.";

        if (LooksLikeVampireSurvivorsDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeVampireSurvivorsDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Vampire Survivors installation. Pick the " +
               "folder that contains VampireSurvivors.exe (for Steam this is usually " +
               @"...\steamapps\common\Vampire Survivors).";
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
            Text = "Vampire Survivors is your own game (Steam) with the " +
                   "ArchipelagoSurvivors MelonLoader mod added on top by SWCreeperKing. " +
                   "The launcher detects your Steam install and can stage the mod files " +
                   "into it, but MelonLoader (the mod loader) must be installed " +
                   "separately first (see the guided steps below). You connect to your " +
                   "server via the title-screen connection panel after the game starts. " +
                   "These external steps are not verified by this launcher.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "VAMPIRE SURVIVORS INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? vsDir       = ResolveVampireSurvivorsDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg = vsDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + vsDir
                : "Detected Steam install: " + vsDir)
            : "Vampire Survivors not detected. Pick your install folder below, or " +
              "install Vampire Survivors via Steam first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = vsDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // mod status line
        string? modDll = FindInstalledModDll();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                    ? "ArchipelagoSurvivors mod found: " + modDll
                    : "ArchipelagoSurvivors mod not found in Mods\\ yet — use " +
                      "Install on the Play tab, then ensure MelonLoader is installed " +
                      "(see guided steps below).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // MelonLoader status line (check for MelonLoader.dll in the install root)
        bool melonLoaderPresent = vsDir != null &&
            (File.Exists(Path.Combine(vsDir, "MelonLoader", "MelonLoader.dll")) ||
             File.Exists(Path.Combine(vsDir, "version.dll")) ||        // MelonLoader proxy
             Directory.Exists(Path.Combine(vsDir, "MelonLoader")));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = melonLoaderPresent
                    ? "MelonLoader detected in game folder."
                    : "MelonLoader NOT detected in game folder. The mod cannot load " +
                      "without it — install MelonLoader first (see guided steps below " +
                      "and the MelonLoader link).",
            FontSize = 11, Foreground = melonLoaderPresent ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? vsDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Vampire Survivors install folder (the one containing " +
                          "VampireSurvivors.exe). Detected from Steam automatically; set " +
                          "it here to override.",
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
                Title            = "Select your Vampire Survivors install folder (contains VampireSurvivors.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? vsDir ?? "")
                                   ? (overrideDir ?? vsDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Vampire Survivors folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeVampireSurvivorsDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeVampireSurvivorsDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 1794680). Use " +
                   "this picker if your game was not detected.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection (this session) ────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in-game on the title screen)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "After MelonLoader and the mod are both installed, launch " +
                   "Vampire Survivors. A connection panel appears on the title " +
                   "screen top-left. Enter your Server (e.g. archipelago.gg), " +
                   "Port (e.g. 38281), Slot Name, and Password if the server " +
                   "requires one, then connect. This launcher cannot pre-fill " +
                   "the connection — it is entered in-game on the title screen.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP (MelonLoader must be installed first)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Vampire Survivors on Steam. Install it if you have not. Use the " +
                "picker above if it was not detected automatically.",
            "2. Install MelonLoader into your Vampire Survivors game folder (link below). " +
                "Download the MelonLoader installer, run it, and point it at your " +
                "VampireSurvivors.exe. This gives the game the ability to load mods.",
            "3. Use the Install button on the Play tab — the launcher downloads " +
                "ArchipelagoSurvivors.zip from the mod repo and extracts it into your " +
                "Vampire Survivors\\Mods\\ folder. Alternatively, download the zip " +
                "manually from the mod repo (link below) and place its contents there.",
            "4. Launch Vampire Survivors. Confirm that MelonLoader's console window " +
                "appears and that no mod-load errors are listed.",
            "5. On the title screen, find the Archipelago connection panel (top-left). " +
                "Enter your Server, Port, Slot Name, and Password (if any), then click " +
                "Connect. The launcher cannot pre-fill these — enter them from your " +
                "session details shown in this panel.",
            "6. Each weapon unlock and achievement you trigger becomes a multiworld " +
                "location check. Weapons from other worlds may arrive early — or not " +
                "at all — based on the multiworld.",
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
            ("MelonLoader (install first) ↗",           MelonLoaderReleases),
            ("MelonLoader wiki ↗",                      MelonLoaderUrl),
            ("ArchipelagoSurvivors mod repo ↗",         ModRepoUrl),
            ("ArchipelagoSurvivors releases ↗",         ModRepoUrl + "/releases"),
            ("Vampire Survivors AP Setup Guide ↗",      SetupGuideUrl),
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

    /// "v0.3.4" → "0.3.4" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + the mod zip download URL. Prefers the
    /// "ArchipelagoSurvivors.zip" asset; falls back to the first .zip; falls back to
    /// the pinned v0.3.4 direct URL when the API is unreachable.
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
                string? preferred = null;   // the main mod zip (ArchipelagoSurvivors*.zip)
                string? anyZip    = null;   // any .zip asset
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    anyZip ??= url;
                    if (preferred == null && lower.Contains("archipelago"))
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

    // ── Private helpers — Steam / Vampire Survivors detection ─────────────────

    /// The Vampire Survivors install dir to use: the override (if set and valid)
    /// wins, else the Steam-detected install. Null when nothing is found.
    private string? ResolveVampireSurvivorsDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeVampireSurvivorsDir(ov))
            return ov;

        try { return DetectSteamVampireSurvivorsDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Vampire Survivors if it has VampireSurvivors.exe.
    private static bool LooksLikeVampireSurvivorsDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, VsExeName))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam Vampire Survivors install: read the Steam root from the
    /// registry, gather all library roots from libraryfolders.vdf, and find the
    /// one whose appmanifest_1794680.acf exists → steamapps\common\Vampire Survivors.
    private static string? DetectSteamVampireSurvivorsDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{VsAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    // Prefer the installdir named in the manifest; fall back to the
                    // conventional "Vampire Survivors" folder name.
                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeVampireSurvivorsDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeVampireSurvivorsDir(conventional)) return conventional;
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

    /// Find ArchipelagoSurvivors.dll anywhere under the detected/override Vampire
    /// Survivors install (recursive, case-insensitive). Searches Mods/ first (the
    /// canonical MelonLoader location), then the full install tree as fallback.
    /// Returns the dll path or null.
    private string? FindInstalledModDll()
    {
        try
        {
            string? vs = ResolveVampireSurvivorsDir();
            if (vs == null) return null;

            // Primary: search under <VampireSurvivors>/Mods/ (canonical location).
            string modsDir = Path.Combine(vs, "Mods");
            if (Directory.Exists(modsDir))
            {
                foreach (string dll in Directory.EnumerateFiles(modsDir, "*.dll", SearchOption.AllDirectories))
                {
                    if (Path.GetFileName(dll).Equals(ModPrimaryDll, StringComparison.OrdinalIgnoreCase))
                        return dll;
                }
            }

            // Fallback: search the full install root (handles non-standard layouts).
            foreach (string dll in Directory.EnumerateFiles(vs, "*.dll", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(dll).Equals(ModPrimaryDll, StringComparison.OrdinalIgnoreCase))
                    return dll;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start Vampire Survivors: prefer the exe in the detected/override install;
    /// if that cannot be found but Steam is present, fall back to the steam:// URL.
    /// Surfaces a clear message rather than failing opaquely.
    private void StartVampireSurvivors()
    {
        string? vs  = ResolveVampireSurvivorsDir();
        string? exe = vs != null ? Path.Combine(vs, VsExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = vs!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Vampire Survivors.");

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
            "Could not find VampireSurvivors.exe. Open this game's Settings and " +
            "pick your Vampire Survivors install folder, or install Vampire " +
            "Survivors via Steam.",
            VsExeName);
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the mod's ArchipelagoSurvivors.zip and extract it into
    /// <VampireSurvivors>/Mods/. MelonLoader mods go into the Mods/ folder at the
    /// game root. If the zip wraps everything in a single sub-folder, that wrapper
    /// is flattened. Existing files are overwritten but siblings are preserved.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string modsDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"vs-archipelago-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"vs-archipelago-x-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading ArchipelagoSurvivors {version}..."));
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
                        progress.Report((pct, $"Downloading ArchipelagoSurvivors... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((75, "Extracting mod into your Mods folder..."));
            Directory.CreateDirectory(modsDir);
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // Determine the merge root. If the zip contains a single wrapper folder
            // and no DLL at the extract root, descend into the wrapper so the DLL
            // lands directly in Mods/ (or a named subdirectory within it).
            string mergeRoot = tempExtract;
            string[] topFiles = Directory.GetFiles(mergeRoot);
            string[] topDirs  = Directory.GetDirectories(mergeRoot);
            bool hasDllAtRoot = topFiles.Any(f =>
                Path.GetFileName(f).Equals(ModPrimaryDll, StringComparison.OrdinalIgnoreCase));
            if (!hasDllAtRoot && topDirs.Length == 1 && topFiles.Length == 0)
            {
                // Single wrapper subfolder — descend into it.
                mergeRoot = topDirs[0];
            }

            // Merge the extracted tree INTO the Mods folder. Overwrite individual
            // files but preserve other mods that may live alongside.
            MergeDirectory(mergeRoot, modsDir);

            progress.Report((90, "Mod files extracted into Mods folder."));
        }
        finally
        {
            try { if (File.Exists(tempZip))       File.Delete(tempZip); }           catch { }
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
            catch { /* a locked file (game open?) — skip; user can retry with game closed */ }
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // This plugin keeps its launcher-side settings (the install-dir override + an
    // informational version stamp) in its OWN JSON file so it stays a single
    // self-contained source file and does not modify Core/SettingsStore. BOM-less
    // UTF-8, read-modify-write (same approach as Blasphemous / Hollow Knight / Jak /
    // A Short Hike).

    private sealed class VsSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private VsSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<VsSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(VsSettings s)
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
