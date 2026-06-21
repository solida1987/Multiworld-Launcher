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
// We also do NOT add any file-level `using X = System.Windows...;` aliases — the
// project's GlobalUsings.cs already aliases the short names, and a second local
// alias would be CS1537 (duplicate alias). Bare names or fully-qualified only.

namespace LauncherV2.Plugins.Webfishing;

// ═══════════════════════════════════════════════════════════════════════════════
// WebfishingPlugin — install / launch for "WEBFISHING" (lamedeveloper, 2024)
// played through the webfishing-ap mod by mwoiii, the in-game Archipelago client
// for WEBFISHING using the GDWeave Godot mod loader. This is a NATIVE
// "ConnectsItself" integration in the same family as the shipped Hollow Knight /
// Noita / Stardew Valley plugins: the game's own GDScript mod speaks to the AP
// server (no emulator, no Lua bridge, no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified online) ──────────────────────
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of WEBFISHING (Steam appid 3146520), and Archipelago support is added on top
// via the GDWeave Godot mod loader + the mwmw.Archipelago mod. The verified facts:
//
//   * THE AP WORLD game string is "WEBFISHING" (the game's own string, as used in
//     the apworld by mwoiii, matching the GitHub repo webfishing-ap). GameId = "webfishing".
//
//   * THE MOD repo is mwoiii/webfishing-ap (GitHub). The mod distributes releases
//     as a "mwmw.Archipelago.zip" archive per release; the zip's contents extract
//     into a "mwmw.Archipelago" folder that goes directly into:
//         <GameDir>/GDWeave/mods/mwmw.Archipelago/
//     The mod is SELF-SUFFICIENT for the GDWeave side — it bundles the AP networking
//     client needed to connect. The only prerequisite is GDWeave itself.
//
//   * GDWeave is the standard Godot mod loader for WEBFISHING, authored by NotNite
//     (GitHub: NotNite/GDWeave). It is installed by extracting GDWeave.zip from its
//     GitHub release into the game's install folder, which places a "GDWeave" folder
//     and a "winmm.dll" (the GDWeave loader hook) next to "webfishing.exe". The
//     direct download URL is:
//         https://github.com/NotNite/GDWeave/releases/latest/download/GDWeave.zip
//     The latest version (v2.0.14, verified 2026-06-14) follows this stable URL.
//
//   * CONNECTION is made IN-GAME: after the mod is installed, a connection panel is
//     available in-game where the player enters server / slot / password before
//     starting a run. There is NO known command-line argument or editable config
//     file that the launcher can pre-write (the mod stores its connection info
//     internally using GDWeave's own config API). So this plugin does NOT attempt a
//     connection prefill — the post-launch note and settings panel state this.
//
//   * The game executable is "webfishing.exe". The process name is "webfishing".
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam WEBFISHING install via the Windows registry (HKCU SteamPath
//      + HKLM WOW6432Node InstallPath), parsing libraryfolders.vdf for every
//      library root and locating steamapps\common\WEBFISHING via appmanifest_
//      3146520.acf. A manual install-dir OVERRIDE (folder picker) is also supported
//      and takes precedence; it is validated (must contain webfishing.exe or the
//      WEBFISHING_Data folder) and persisted in this plugin's OWN sidecar
//      (Games/ROMs/webfishing/webfishing_launcher.json).
//   2. INSTALL/UPDATE = two-phase automatic download:
//      Phase A: if GDWeave is not present (no GDWeave/ folder next to webfishing.exe
//               and no winmm.dll hook), download GDWeave.zip from NotNite's release
//               and extract it into the game folder.
//      Phase B: download the latest mwmw.Archipelago.zip from mwoiii/webfishing-ap
//               releases (GitHub API), extract into GDWeave/mods/mwmw.Archipelago/.
//      Both phases are idempotent and can be called again to update.
//   3. LAUNCH = start webfishing.exe from the detected/override install; if that
//      cannot be found but Steam is present, fall back to steam://rungameid/3146520.
//      ConnectsItself = true (the mod owns the slot — the launcher must NOT hold
//      its own ApClient on it). SupportsStandalone = true (plain WEBFISHING runs
//      without AP). No prefill (in-game panel), stated honestly.
//
// ── DEFENSIVE / UNVERIFIED ───────────────────────────────────────────────────
//   * "Installed" is judged by the presence of both the GDWeave loader hook
//     (winmm.dll next to webfishing.exe OR the GDWeave/ folder) AND the mod folder
//     (GDWeave/mods/mwmw.Archipelago/) containing at least one file.
//   * InstalledVersion is read from the mod's manifest.json if present.
//   * Steam library parsing is defensive: a tolerant VDF scan; any failure degrades
//     to "WEBFISHING not found" rather than throwing.
//   * No plaintext AP password is ever written by this plugin (entered in-game),
//     so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class WebfishingPlugin : IGamePlugin
{
    // ── Constants — GDWeave loader (verified 2026-06-14) ─────────────────────
    private const string GDWEAVE_OWNER      = "NotNite";
    private const string GDWEAVE_REPO       = "GDWeave";
    private const string GdWeaveRepoUrl     = $"https://github.com/{GDWEAVE_OWNER}/{GDWEAVE_REPO}";
    private const string GdWeaveZipUrl      = $"https://github.com/{GDWEAVE_OWNER}/{GDWEAVE_REPO}/releases/latest/download/GDWeave.zip";
    private const string GH_GDWEAVE_API_URL =
        $"https://api.github.com/repos/{GDWEAVE_OWNER}/{GDWEAVE_REPO}/releases/latest";

    // ── Constants — the AP mod (verified 2026-06-14) ─────────────────────────
    private const string MOD_OWNER  = "mwoiii";
    private const string MOD_REPO   = "webfishing-ap";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    // Thunderstore page — useful secondary link for the user.
    private const string ThunderstoreModUrl =
        "https://thunderstore.io/c/webfishing/p/mwmw/Archipelago/";

    private const string SetupGuideUrl  = "https://archipelago.gg/tutorial/WEBFISHING/setup/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — WEBFISHING appid 3146520.
    private const string SteamAppId          = "3146520";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    /// The conventional Steam steamapps\common sub-folder name.
    private const string SteamCommonFolderName = "WEBFISHING";

    /// The GDWeave "mods" sub-folder name (relative to game dir).
    private const string GdWeaveModsFolderName = "GDWeave";

    /// The mod's folder name inside GDWeave/mods/ (the GDWeave package ID).
    private const string ApModFolderName = "mwmw.Archipelago";

    /// The GDWeave hook DLL placed next to webfishing.exe on install.
    private const string GdWeaveHookDll = "winmm.dll";

    /// Pinned fallback mod version in case the GitHub API is unreachable.
    private const string FallbackModVersion = "1.2.2-alpha";
    private const string FallbackModZipName = "mwmw.Archipelago.zip";
    private static readonly string FallbackModZipUrl =
        $"{ModRepoUrl}/releases/download/v{FallbackModVersion}/{FallbackModZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "webfishing";
    public string DisplayName => "WEBFISHING";
    public string Subtitle    => "Native PC · GDWeave mod";

    /// AP game string — "WEBFISHING" (the game's title as used in the apworld by
    /// mwoiii/webfishing-ap, verified 2026-06-14).
    public string ApWorldName => "WEBFISHING";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "webfishing.png");

    public string ThemeAccentColor => "#1E8840";   // fishing / nature green
    public string[] GameBadges     => new[] { "Steam", "GDWeave", "ConnectsItself" };

    public string Description =>
        "WEBFISHING Archipelago randomizer using the GDWeave mod loader for Godot. " +
        "Checks come from purchases, quests, journal entries and more; victory is " +
        "reaching a target rank, a journal completion percentage, or unlocking the " +
        "final camp tier. The mod is multiplayer-compatible — you can fish alongside " +
        "friends regardless of whether they have it installed. This launcher installs " +
        "GDWeave and the AP mod automatically, then launches the game. You connect to " +
        "your Archipelago server from the in-game connection panel.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    public bool IsInstalled => CheckIsInstalled();

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Exposed as GameDirectory for the IGamePlugin contract. Points at the
    /// detected/override WEBFISHING install folder (or the working-files folder
    /// when the game has not been found yet).
    public string GameDirectory
    {
        get
        {
            string? resolved = ResolveGameDir();
            return resolved ?? Path.Combine(AppContext.BaseDirectory, "Games", "Webfishing");
        }
        set { /* no-op: directory comes from Steam detection / settings override */ }
    }

    /// This plugin's OWN settings sidecar — same pattern as Noita/Raft/HK.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "webfishing_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The mwmw.Archipelago mod reports checks/items/goal to the AP server itself —
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
            // InstalledVersion: read from the mod's manifest.json; fall back to
            // "installed" if the mod folder exists but has no manifest.
            if (CheckIsInstalled())
            {
                InstalledVersion = ReadModManifestVersion() ?? "installed";
            }
            else
            {
                InstalledVersion = null;
            }
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
        // 0. Locate the WEBFISHING install. We need it to know where to drop
        //    GDWeave and the mod.
        progress.Report((2, "Locating your WEBFISHING installation..."));
        string? gameDir = ResolveGameDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a WEBFISHING installation. Open this game's Settings " +
                "and pick your WEBFISHING folder (the one containing webfishing.exe), " +
                "or install WEBFISHING via Steam first.");

        // ── Phase A: Install / update GDWeave if not present ─────────────────
        string gdWeaveDir  = Path.Combine(gameDir, GdWeaveModsFolderName);
        string hookDllPath = Path.Combine(gameDir, GdWeaveHookDll);
        bool gdWeavePresent = Directory.Exists(gdWeaveDir) || File.Exists(hookDllPath);

        if (!gdWeavePresent)
        {
            progress.Report((5, "GDWeave not found — downloading GDWeave mod loader..."));
            await DownloadAndExtractZipAsync(
                GdWeaveZipUrl,
                gameDir,
                stripSingleTopFolder: false,
                progress,
                ct,
                pctStart: 5,
                pctEnd: 40,
                label: "GDWeave");

            progress.Report((42, "GDWeave installed."));
        }
        else
        {
            progress.Report((40, "GDWeave already present — skipping GDWeave download."));
        }

        // ── Phase B: Install / update the AP mod ─────────────────────────────
        progress.Report((45, "Checking the latest WEBFISHING Archipelago mod release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the WEBFISHING Archipelago mod download on GitHub. " +
                "Check your internet connection, or download it manually from:\n" +
                ModRepoUrl + "/releases");

        string modsDir  = Path.Combine(gameDir, GdWeaveModsFolderName, "mods");
        string apModDir = Path.Combine(modsDir, ApModFolderName);
        Directory.CreateDirectory(modsDir);

        // Clear the old mod folder first so we get a clean update.
        if (Directory.Exists(apModDir))
        {
            try { Directory.Delete(apModDir, recursive: true); } catch { /* non-fatal */ }
        }
        Directory.CreateDirectory(apModDir);

        progress.Report((50, $"Downloading AP mod {version}..."));
        await DownloadAndExtractZipAsync(
            zipUrl,
            apModDir,
            stripSingleTopFolder: true,
            progress,
            ct,
            pctStart: 50,
            pctEnd: 92,
            label: $"AP mod {version}");

        // Stamp the version for the tile display.
        WriteStampedVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            $"WEBFISHING Archipelago mod {version} installed successfully.\n" +
            "GDWeave is installed next to webfishing.exe. The mod is in:\n" +
            apModDir + "\n\n" +
            "To play: launch WEBFISHING, then enter your Archipelago server, slot " +
            "name, and password in the in-game connection panel. The launcher cannot " +
            "pre-fill the connection — it is entered in-game."));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return CheckIsInstalled();
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // HONEST: the AP server connection for WEBFISHING is entered in the IN-GAME
        // connection panel. There is no command-line / config prefill we can apply
        // (verified — GDWeave mods store config through their own internal GDWeave
        // config API, not a plain editable file outside the game). So launching from
        // this tile just starts the game; the user connects in-game.
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartWebfishing();
        return Task.CompletedTask;
    }

    /// Plain WEBFISHING runs fine without the mod.
    public bool SupportsStandalone => true;

    /// The mwmw.Archipelago mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartWebfishing();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself) ───────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The mwmw.Archipelago mod receives items from the AP server directly;
        // there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// A valid WEBFISHING folder contains webfishing.exe and/or WEBFISHING_Data/.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your WEBFISHING install folder.";

        if (LooksLikeWebfishingDir(folder))
            return null;

        // Be forgiving: user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeWebfishingDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a WEBFISHING installation. Pick the folder " +
               "that contains webfishing.exe (or the WEBFISHING_Data folder).";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20)
        };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "WEBFISHING is your own game on Steam. The launcher installs GDWeave " +
                   "(the Godot mod loader) and the Archipelago mod (mwmw.Archipelago) " +
                   "automatically via the Install button. Connection details are entered " +
                   "in-game — the launcher cannot pre-fill them.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: WEBFISHING install ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "WEBFISHING INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? gameDir     = ResolveGameDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg   = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "WEBFISHING not detected. Pick your install folder below, or install it " +
              "via Steam first.";

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // ── GDWeave status ────────────────────────────────────────────────
        bool gdWeaveOk = gameDir != null && IsGdWeavePresent(gameDir);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = gdWeaveOk
                    ? "GDWeave: installed (" + (gameDir != null ? Path.Combine(gameDir, GdWeaveModsFolderName) : "") + ")"
                    : "GDWeave: not found. The Install button will download and install it automatically.",
            FontSize = 11, Foreground = gdWeaveOk ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // ── Mod status ────────────────────────────────────────────────────
        bool modOk = gameDir != null && IsApModPresent(gameDir);
        string? modVersion = modOk ? (ReadModManifestVersion() ?? InstalledVersion ?? "installed") : null;
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modOk
                    ? $"Archipelago mod: installed{(modVersion != null ? " (" + modVersion + ")" : "")} — " +
                      Path.Combine(gameDir!, GdWeaveModsFolderName, "mods", ApModFolderName)
                    : "Archipelago mod (mwmw.Archipelago): not found. Click Install on the Play tab.",
            FontSize = 11, Foreground = modOk ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 10),
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
            ToolTip     = "Your WEBFISHING install folder (the one containing webfishing.exe). " +
                          "Detected from Steam automatically; use this picker to override.",
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
                Title            = "Select your WEBFISHING install folder (contains webfishing.exe)",
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
                    System.Windows.MessageBox.Show(bad, "Not a WEBFISHING folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // Descend into game folder if user picked the Steam "common" parent.
                if (!LooksLikeWebfishingDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeWebfishingDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 3146520). Use this " +
                   "picker for a non-standard Steam library location.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 14),
        });

        // ── Section: Connection instructions ──────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING TO ARCHIPELAGO", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "After launching WEBFISHING, enter your server address, slot name, and " +
                   "password in the in-game Archipelago connection panel. The launcher " +
                   "cannot pre-fill these fields — they are entered in-game via the mod's " +
                   "own UI.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own WEBFISHING on Steam (appid 3146520). Install it and launch it once " +
                "to make sure it runs before modding.",
            "2. Click Install on the Play tab. The launcher will download GDWeave (the " +
                "Godot mod loader for WEBFISHING) and then download and install the " +
                "Archipelago mod (mwmw.Archipelago) automatically.",
            "3. You can also install GDWeave manually: download GDWeave.zip from the " +
                "GDWeave GitHub releases page and extract it into your WEBFISHING folder " +
                "(next to webfishing.exe). Then extract the mod into GDWeave/mods/mwmw.Archipelago/.",
            "4. Generate your WEBFISHING Archipelago game on archipelago.gg, download the " +
                "apworld from the mod's GitHub releases page (for custom_worlds/), and get " +
                "your YAML from the same release.",
            "5. Launch WEBFISHING from this launcher (or from Steam — GDWeave loads " +
                "automatically via winmm.dll). The Archipelago mod's connection UI will " +
                "appear in-game.",
            "6. In-game, enter your Archipelago server address (e.g. archipelago.gg:38281), " +
                "your slot name, and your password (if any) into the mod's connection panel, " +
                "then start your game.",
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
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("webfishing-ap (GitHub — mod + apworld releases) ↗", ModRepoUrl),
            ("mwmw/Archipelago (Thunderstore) ↗",                  ThunderstoreModUrl),
            ("GDWeave mod loader (GitHub) ↗",                      GdWeaveRepoUrl),
            ("Archipelago Official ↗",                              ArchipelagoSite),
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
                Cursor          = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); }
                catch { /* ignore */ }
            };
            panel.Children.Add(btn);
        }

        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // Return an empty list — the AP mod's news is sparse and alpha; we don't
        // want to surface pre-release churn as "news" to the user.
        await Task.CompletedTask;
        return Array.Empty<NewsItem>();
    }

    // ── Private helpers — install detection ───────────────────────────────────

    /// True when both GDWeave and the AP mod are present in a detected/override
    /// WEBFISHING install.
    private bool CheckIsInstalled()
    {
        try
        {
            string? dir = ResolveGameDir();
            if (dir == null) return false;
            return IsGdWeavePresent(dir) && IsApModPresent(dir);
        }
        catch { return false; }
    }

    /// GDWeave is "present" when its sub-folder or the loader hook DLL exists.
    private static bool IsGdWeavePresent(string gameDir)
    {
        try
        {
            if (Directory.Exists(Path.Combine(gameDir, GdWeaveModsFolderName))) return true;
            if (File.Exists(Path.Combine(gameDir, GdWeaveHookDll)))             return true;
            return false;
        }
        catch { return false; }
    }

    /// The AP mod is "present" when its mods sub-folder exists and is non-empty.
    private static bool IsApModPresent(string gameDir)
    {
        try
        {
            string modDir = Path.Combine(gameDir, GdWeaveModsFolderName, "mods", ApModFolderName);
            if (!Directory.Exists(modDir)) return false;
            // At least one file must be there (guards against an empty directory).
            return Directory.EnumerateFiles(modDir, "*", SearchOption.AllDirectories).Any();
        }
        catch { return false; }
    }

    /// Read the mod version from GDWeave/mods/mwmw.Archipelago/manifest.json if
    /// present. GDWeave manifests are JSON objects with a "Version" (or "version")
    /// string field. Falls back to null on any parse failure.
    private string? ReadModManifestVersion()
    {
        try
        {
            string? dir = ResolveGameDir();
            if (dir == null) return null;

            string manifestPath = Path.Combine(
                dir, GdWeaveModsFolderName, "mods", ApModFolderName, "manifest.json");
            if (!File.Exists(manifestPath)) return null;

            using var doc  = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = doc.RootElement;

            // Try common casing variants used in GDWeave manifests.
            foreach (string key in new[] { "Version", "version", "ModVersion", "mod_version" })
            {
                if (root.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String)
                {
                    string? v = val.GetString();
                    if (!string.IsNullOrWhiteSpace(v))
                        return v.Trim();
                }
            }
        }
        catch { /* non-fatal */ }
        return null;
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    /// "v1.2.3-alpha" → "1.2.3-alpha" when a leading 'v' decorates a digit; else
    /// trimmed. Pre-release tags are kept as-is (e.g. "1.2.2-alpha").
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + download URL of the
    /// "mwmw.Archipelago.zip" asset. Falls back to the pinned constant URL.
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
                string? preferred = null;   // the mwmw.Archipelago*.zip
                string? anyZip    = null;   // any .zip as fallback
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name",                 out var n) ? n.GetString() : null;
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

        return (FallbackModVersion, FallbackModZipUrl);
    }

    // ── Private helpers — download / extract ──────────────────────────────────

    /// Download a ZIP from `zipUrl` and extract into `destDir`.
    /// When `stripSingleTopFolder` is true and the zip contains exactly one
    /// top-level folder, the contents of that folder are extracted directly into
    /// `destDir` (flattening the wrapper folder — common in GDWeave mod zips).
    private static async Task DownloadAndExtractZipAsync(
        string zipUrl,
        string destDir,
        bool   stripSingleTopFolder,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct,
        int pctStart,
        int pctEnd,
        string label)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"wf-ap-{label.Replace(" ", "_")}-{Guid.NewGuid():N}.zip");
        try
        {
            // ── Download ──────────────────────────────────────────────────────
            using var response = await _http.GetAsync(
                zipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long total      = response.Content.Headers.ContentLength ?? -1;
            long downloaded = 0;
            int  dlRange    = (pctStart + pctEnd) / 2 - pctStart;  // half the range for DL

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
                        int pct = pctStart + (int)(dlRange * downloaded / total);
                        progress.Report((pct,
                            $"Downloading {label}... {downloaded / 1024}KB / {total / 1024}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            // ── Extract ───────────────────────────────────────────────────────
            int extractPct = (pctStart + pctEnd) / 2;
            progress.Report((extractPct, $"Extracting {label}..."));

            Directory.CreateDirectory(destDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(
                tempZip, destDir, overwriteFiles: true);

            // Strip single top-level wrapper folder if requested (the mod zip
            // often has a "mwmw.Archipelago/" wrapping everything inside it).
            if (stripSingleTopFolder)
            {
                string[] topDirs  = Directory.GetDirectories(destDir);
                string[] topFiles = Directory.GetFiles(destDir);
                if (topDirs.Length == 1 && topFiles.Length == 0)
                {
                    string wrapper = topDirs[0];
                    foreach (string srcFile in Directory.EnumerateFiles(
                                 wrapper, "*", SearchOption.AllDirectories))
                    {
                        string rel    = Path.GetRelativePath(wrapper, srcFile);
                        string dstFile = Path.Combine(destDir, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(dstFile)!);
                        File.Move(srcFile, dstFile, overwrite: true);
                    }
                    try { Directory.Delete(wrapper, recursive: true); } catch { }
                }
            }

            progress.Report((pctEnd, $"{label} extracted."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    // ── Private helpers — Steam / WEBFISHING detection ────────────────────────

    /// The game install dir to use: the override (if set and valid) wins, else
    /// the Steam-detected install. Null when nothing is found.
    private string? ResolveGameDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeWebfishingDir(ov))
            return ov;

        try { return DetectSteamWebfishingDir(); }
        catch { return null; }
    }

    /// A folder "looks like" WEBFISHING if it has webfishing.exe and/or the
    /// WEBFISHING_Data folder (Godot export layout).
    private static bool LooksLikeWebfishingDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, "webfishing.exe")))          return true;
            // WEBFISHING ships as a Godot game; the data folder may be named
            // "WEBFISHING.pck" (single-file export) or have a _Data folder.
            if (Directory.Exists(Path.Combine(dir, "WEBFISHING_Data")))    return true;
            if (File.Exists(Path.Combine(dir, "WEBFISHING.pck")))          return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam WEBFISHING install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_3146520.acf exists → steamapps\common\WEBFISHING.
    private static string? DetectSteamWebfishingDir()
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
                        if (LooksLikeWebfishingDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeWebfishingDir(conventional)) return conventional;
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    /// Candidate Steam install roots (HKCU SteamPath + HKLM WOW6432Node + HKLM
    /// + %ProgramFiles(x86)%\Steam). Duplicates are harmless.
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

        foreach (string p in ExtractVdfPaths(text))
        {
            string norm = p.Replace('/', '\\').TrimEnd('\\');
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

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start WEBFISHING: prefer webfishing.exe from the detected/override install;
    /// fall back to steam://rungameid/3146520. GDWeave loads automatically via
    /// winmm.dll — no special launcher needed.
    private void StartWebfishing()
    {
        string? dir = ResolveGameDir();
        string? exe = dir != null ? Path.Combine(dir, "webfishing.exe") : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = dir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start WEBFISHING.");

            TrackProcess(proc);
            return;
        }

        // Fall back to Steam if we can see Steam installed.
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
            "Could not find webfishing.exe. Open this game's Settings and pick your " +
            "WEBFISHING install folder, or install WEBFISHING via Steam.",
            "webfishing.exe");
    }

    /// Wire up process tracking and exit notification for a launched process.
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
                GameExited?.Invoke(proc.ExitCode);
            };
        }
        catch { /* some processes don't expose Exited — non-fatal */ }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // This plugin stores its launcher-side settings (install-dir override + a
    // version stamp) in its OWN JSON file — same pattern as Noita/Raft/HK.

    private sealed class WebfishingSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private WebfishingSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<WebfishingSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(WebfishingSettings s)
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
        var s = LoadSettings();
        s.InstallOverride = p;
        SaveSettings(s);
    }

    private void WriteStampedVersion(string v)
    {
        var s = LoadSettings();
        s.ModVersion = v;
        SaveSettings(s);
    }
}
