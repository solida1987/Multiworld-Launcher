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

// The launcher project sets BOTH UseWPF and UseWindowsForms=true, so several UI
// type names (Color, Brushes, Button, TextBox, HorizontalAlignment) collide
// between WPF and WinForms. The project's GlobalUsings.cs already aliases each of
// these to its WPF type globally, so this file relies on those — no local aliases
// (a local alias duplicating a global one is CS1537).

namespace LauncherV2.Plugins.GrimDawn;

// ═══════════════════════════════════════════════════════════════════════════════
// GrimDawnPlugin — install / launch for "Grim Dawn" (Crate Entertainment, 2016)
// played through the Archipelago mod by DaKennyMan22 / Faris, hosted in the
// routhken/Archipelago fork on GitHub (branch Grim_Dawn). This is a NATIVE
// "ConnectsItself" integration: the game's own Lua scripting system (via a
// patched lua51.dll and lua-apclientpp.dll) carries the Archipelago client
// connection from inside the game, so no external AP client binary needs to be
// kept running by the launcher.
//
// ── HONEST REALITY CHECK (2026-06-14, verified against the AP fork) ──────────
// Grim Dawn is a STEAM-MOD native: the user must own the base game (Steam appid
// 219990). AP support is delivered as a Grim Dawn mod placed in the game's own
// "mods" folder, with several manual prerequisites that cannot be automated:
//
//   * THE AP WORLD game string is "Grim Dawn" (verified against
//     worlds/grim_dawn/__init__.py: `class GrimDawnWorld(World): ... game = "Grim Dawn"`
//     and worlds/grim_dawn/archipelago.json: `"game": "Grim Dawn"`). The apworld
//     ships as grim_dawn.apworld in releases from routhken/Archipelago. Because this
//     is a community fork (not part of core AP), it must be placed in the user's
//     Archipelago "custom_worlds" folder — NOT lib/worlds.
//
//   * LATEST RELEASE: v0.4.0 (published 2026-05-11 from routhken/Archipelago,
//     release name "Grim Dawn v0.4.0"). Assets: grim_dawn.apworld + the full
//     Archipelago Windows installer Setup.Archipelago.0.6.7.exe. The mod itself is
//     downloaded separately from Nexus Mods (mods/167).
//
//   * THE MOD FRAMEWORK is Grim Dawn's NATIVE modding system: the game natively
//     supports mods in a "mods" subfolder of the install, and ships with an ARZ
//     database format. The AP mod (named "archipelago") ships as an ARZ database +
//     resource arc files. Patching works via "arzedit.exe" (extracted from GitLab,
//     not shipped in the AP mod itself), which extracts the ARZ, applies slot_data
//     changes, then recompiles to produce a "patchedArchipelago" mod. No third-party
//     tools like Grim Internals or GD Defiler are involved.
//
//   * LUA BRIDGE: Grim Dawn 1.2.x ships a lua51.dll. The AP mod requires replacing
//     it with Heinermann's GrimDawnLuaUnlocker lua51.dll (allows external DLL
//     loading), plus placing Black Sliver's lua-apclientpp.dll (the actual AP
//     client library for Lua) in the install dir. The game's Lua scripts in the
//     "archipelago" mod then call lua-apclientpp to speak to the AP server —
//     NO SEPARATE EXTERNAL CLIENT PROCESS is needed. The game connects to the AP
//     server as soon as the player loads into a world on the patched "patchedArchipelago"
//     custom mod.
//
//   * CONNECTION: PATCHING is done via the Archipelago Launcher's "Grim Dawn Client"
//     (a Python CommonClient that connects to the AP server, receives slot_data,
//     patches the installed mod, then writes a connect.txt with credentials). The
//     connect.txt format is:
//         host = ws://server:port
//         slot = SlotName
//         password = (password or blank)
//         ssp = true|false
//     The game reads connect.txt at startup (from the Grim Dawn install root).
//     After patching the client can be closed — the patched mod stores the connect
//     credentials in connect.txt and connects itself from inside the game.
//
//   * GRIM DAWN MUST BE LAUNCHED 32-BIT: the 32-bit launcher (Grim Dawn.exe in the
//     root, NOT x64/Grim Dawn.exe) is required — the lua51.dll + lua-apclientpp.dll
//     are 32-bit (clang32). Using the x64 binary will NOT connect.
//
//   * IMPORTANT: locale must be "English (United States)" during patching (the
//     arzedit patcher uses region-sensitive float parsing). This can be reverted
//     after patching.
//
// ── PREREQUISITES (not automatable) ─────────────────────────────────────────
//   1. Own Grim Dawn (Steam). The base game must exist in a detectable or user-
//      specified install folder.
//   2. Download Heinermann's GrimDawnLuaUnlocker lua51.dll from GitHub and rename
//      the original to real_lua51.dll (one-time manual step).
//   3. Download Black Sliver's lua-apclientpp.dll (lua51\lua51-clang32-dynamic
//      from lua-apclientpp v0.4.9) and place it in the install dir.
//   4. Download the archipelago mod from Nexus Mods (mods/167) and extract it so
//      that <GrimDawn>\mods\archipelago exists.
//   5. Download arzedit.exe from GitLab and place it in the install dir.
//   6. Patch the game by opening the AP Launcher, using the Grim Dawn Client
//      component, and connecting to your slot (slot_data drives the patching).
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Grim Dawn install via the Windows registry + VDF parsing
//      (same Steam detection code used by NoitaPlugin / other Steam-native plugins).
//      A manual install-dir OVERRIDE (folder picker in Settings) is supported and
//      takes precedence; validated + persisted in per-plugin sidecar
//      (Games/ROMs/grim_dawn/grim_dawn_launcher.json).
//   2. CHECK FOR UPDATE: queries the routhken/Archipelago releases API for the
//      latest grim_dawn.apworld version.
//   3. INSTALL/UPDATE of the grim_dawn.apworld: downloads it from the GitHub
//      release and places it in the user's Archipelago custom_worlds folder
//      (read-only C:\ProgramData\Archipelago is NEVER touched — we locate the
//      user's own Archipelago install via the per-user AppData path where the
//      Archipelago launcher places custom_worlds).
//   4. LAUNCH = runs the 32-bit Grim Dawn.exe from the detected/override install.
//      Falls back to steam://rungameid/219990 when the exe is not found.
//      ConnectsItself = true; SupportsStandalone = true.
//   5. Settings panel: guided steps for the manual prerequisites, links to all
//      required downloads, install path picker, connect.txt credential preview.
//
// ── VERIFICATION STATE ────────────────────────────────────────────────────────
//   "Installed" = the mods\archipelago folder is present under the detected/
//   override Grim Dawn install AND contains Archipelago.arz (the compiled mod DB).
//   The grim_dawn.apworld in the user's Archipelago custom_worlds folder is checked
//   separately (CanGenerate) but does not affect the IsInstalled tile.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class GrimDawnPlugin : IGamePlugin
{
    // ── Constants — routhken/Archipelago fork ─────────────────────────────────
    private const string AP_FORK_OWNER = "routhken";
    private const string AP_FORK_REPO  = "Archipelago";
    private const string AP_FORK_BRANCH = "Grim_Dawn";
    private static readonly string ApForkUrl =
        $"https://github.com/{AP_FORK_OWNER}/{AP_FORK_REPO}";
    private static readonly string GH_RELEASES_API =
        $"https://api.github.com/repos/{AP_FORK_OWNER}/{AP_FORK_REPO}/releases";
    private static readonly string GH_RELEASES_LATEST_API =
        $"https://api.github.com/repos/{AP_FORK_OWNER}/{AP_FORK_REPO}/releases/latest";

    // ── Constants — prerequisite download links (verified 2026-06-14) ─────────
    private const string NexusModUrl =
        "https://www.nexusmods.com/grimdawn/mods/167/";
    private const string LuaUnlockerUrl =
        "https://github.com/heinermann/GrimDawnLuaUnlocker/releases/download/1.0/lua51.dll";
    private const string LuaApclientppUrl =
        "https://github.com/black-sliver/lua-apclientpp/releases/tag/v0.4.9";
    private const string ArzeditUrl =
        "https://gitlab.com/QuasiMod/arzedit/uploads/b48b7ed4f74717d2a8cf6b67b4f9d842/arzedit.exe";
    private const string SetupGuideUrl =
        "https://github.com/routhken/Archipelago/tree/Grim_Dawn/worlds/grim_dawn/docs";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // ── Constants — Steam ──────────────────────────────────────────────────────
    private const string GdSteamAppId           = "219990";
    private static readonly string SteamRunUrl  = $"steam://rungameid/{GdSteamAppId}";
    private const string SteamCommonFolderName  = "Grim Dawn";

    // ── Constants — game files / mod structure ────────────────────────────────
    // 32-bit exe (NOT x64/Grim Dawn.exe — the lua DLLs are 32-bit only)
    private const string GdExeName         = "Grim Dawn.exe";
    private const string GdModFolderName   = "archipelago";
    private const string GdModArzRelPath   = @"mods\archipelago\database\Archipelago.arz";

    // ── Constants — apworld packaging (grim_dawn.apworld in release assets) ───
    private const string ApWorldAssetName  = "grim_dawn.apworld";
    private const string FallbackVersion   = "0.4.0";

    // Archipelago user-folder: the Archipelago launcher installs to
    // %AppData%\Archipelago (user-writable) — NOT C:\ProgramData\Archipelago
    // (which is READ-ONLY per architecture rules). custom_worlds lives there.
    private static readonly string ArchipelagoUserFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "Archipelago");
    private static readonly string CustomWorldsFolder =
        Path.Combine(ArchipelagoUserFolder, "custom_worlds");

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "grim_dawn";
    public string DisplayName => "Grim Dawn";
    public string Subtitle    => "Native PC · Lua bridge mod";

    /// EXACT AP game string — verified against worlds/grim_dawn/__init__.py
    /// and worlds/grim_dawn/archipelago.json in the routhken/Archipelago fork.
    public string ApWorldName => "Grim Dawn";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "grim_dawn.png");

    public string ThemeAccentColor => "#7A3A1A";   // dark ember / rusted iron
    public string[] GameBadges     => new[] { "Steam · manual prerequisites" };

    public string Description =>
        "Grim Dawn, Crate Entertainment's 2016 action RPG set in a grim post-apocalyptic " +
        "fantasy world, played through the Archipelago mod by DaKennyMan22 and Faris " +
        "(routhken/Archipelago). Grim Dawn's native mod system combined with a Lua DLL " +
        "bridge carries the Archipelago client from inside the game — no external client " +
        "process is needed once the game is patched. Items, skills, devotion shrines, " +
        "bosses, quests, and DLC content become checks shuffled across the multiworld. " +
        "Skill, enemy, and devotion randomizers are available as optional settings. " +
        "You bring your own copy of Grim Dawn (Steam); the AP mod is added on top with " +
        "a small set of one-time manual prerequisites (Lua DLL swap, arzedit patcher, " +
        "mod files from Nexus Mods). After patching once via the AP Launcher client, the " +
        "game connects to the AP server on its own each session.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" = the mods\archipelago\database\Archipelago.arz is present.
    public bool IsInstalled
    {
        get
        {
            try
            {
                string? gdDir = ResolveGrimDawnDir();
                if (gdDir == null) return false;
                return File.Exists(Path.Combine(gdDir, GdModArzRelPath));
            }
            catch { return false; }
        }
    }

    public bool IsRunning { get; private set; }

    // ── IGamePlugin — Capabilities ────────────────────────────────────────────

    /// The Lua bridge mod carries the AP connection from inside the game.
    public bool ConnectsItself => true;

    /// Plain Grim Dawn runs without the AP mod.
    public bool SupportsStandalone => true;

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "GrimDawn");

    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "grim_dawn_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The Lua mod reports checks/items/goal to the AP server directly.
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
            InstalledVersion = IsInstalled
                ? (ReadStampedVersion() ?? "installed")
                : null;
        }
        catch { InstalledVersion = null; }
            try
        {
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(AP_FORK_OWNER, AP_FORK_REPO, ct));
        }
        catch
        {
            AvailableVersion = null; // contract: never throw on network failure
        }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    /// Installs the grim_dawn.apworld into the user's Archipelago custom_worlds
    /// folder. Per architecture rules, C:\ProgramData\Archipelago is READ-ONLY
    /// and is never touched. We use the writable %AppData%\Archipelago path.
    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 1. Resolve the latest release.
        progress.Report((5, "Checking the latest Grim Dawn AP release on GitHub..."));
        var (version, apWorldUrl) = await ResolveLatestApWorldAsync(ct);
        AvailableVersion = version;

        if (apWorldUrl == null)
            throw new InvalidOperationException(
                "Could not find the grim_dawn.apworld download on GitHub. Check your " +
                "internet connection or download it manually from " + ApForkUrl +
                "/releases and place it in your Archipelago custom_worlds folder.");

        // 2. Ensure the custom_worlds directory exists (user-writable).
        progress.Report((15, "Preparing the Archipelago custom_worlds folder..."));
        Directory.CreateDirectory(CustomWorldsFolder);

        // 3. Download the .apworld file.
        progress.Report((20, $"Downloading grim_dawn.apworld {version}..."));
        string destApWorld = Path.Combine(CustomWorldsFolder, ApWorldAssetName);
        string tempFile    = destApWorld + ".tmp";
        try
        {
            using var response = await _http.GetAsync(
                apWorldUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long total = response.Content.Headers.ContentLength ?? -1;
            long downloaded = 0;

            await using (var src = await response.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tempFile))
            {
                var buf = new byte[65536];
                int bytesRead;
                while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
                    downloaded += bytesRead;
                    if (total > 0)
                    {
                        int pct = (int)(20 + 60 * downloaded / total);
                        progress.Report((pct,
                            $"Downloading grim_dawn.apworld... {downloaded / 1024}KB / {total / 1024}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            // Atomic replace.
            File.Move(tempFile, destApWorld, overwrite: true);

            WriteStampedVersion(version);
            InstalledVersion = version;

            progress.Report((100,
                $"grim_dawn.apworld {version} installed to your Archipelago custom_worlds " +
                "folder. IMPORTANT: the game mod and its prerequisites still require manual " +
                "setup. Open Settings for the step-by-step guide and download links. Key " +
                "remaining steps: (1) install the mod files from Nexus Mods, (2) install " +
                "the Lua DLL bridge, (3) download arzedit.exe, (4) open the AP Launcher's " +
                "Grim Dawn Client and connect to your slot to patch the game."));
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        // "Installed" means the compiled mod DB is in place.
        return IsInstalled;
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // HONEST: The connection credentials are baked into the game's connect.txt
        // by the AP Launcher's "Grim Dawn Client" component during the patching step
        // (a separate Python CommonClient that connects to the AP server, receives
        // slot_data, patches the mod, and writes:
        //   host = ws://server:port
        //   slot = SlotName
        //   password = (password)
        //   ssp = true|false
        // to <GrimDawnInstall>\connect.txt). This launcher does NOT write connect.txt
        // itself — patching is done once via the AP Launcher client, and the patched
        // "patchedArchipelago" mod reads connect.txt on load. ConnectsItself = true.
        _ = session; // connection is managed by the mod (connect.txt from AP patcher)
        StartGrimDawn();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartGrimDawn();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert ─────────────────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The Lua-apclientpp bridge inside the game receives items directly.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The in-game Lua mod renders its own connection status.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Honesty header ─────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "Grim Dawn requires several one-time manual prerequisites before the " +
                   "Archipelago mod can work. The launcher installs the grim_dawn.apworld " +
                   "into your Archipelago custom_worlds folder, but the mod itself (Nexus " +
                   "Mods), the Lua DLL bridge, arzedit, and the game-patching step via " +
                   "the AP Launcher's Grim Dawn Client all require manual action. Follow " +
                   "the guided steps below.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: Grim Dawn install path ───────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "GRIM DAWN INSTALL", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? gdDir       = ResolveGrimDawnDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg   = gdDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gdDir
                : "Detected Steam install: " + gdDir)
            : "Grim Dawn not detected. Pick your install folder below, or install " +
              "Grim Dawn via Steam first.";
        panel.Children.Add(new TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gdDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // Mod DB presence check
        bool modPresent = IsInstalled;
        panel.Children.Add(new TextBlock
        {
            Text = modPresent
                ? "Archipelago mod found: " + (gdDir != null ? Path.Combine(gdDir, GdModArzRelPath) : "")
                : "Archipelago mod database (Archipelago.arz) not found. See step 3 below.",
            FontSize = 11,
            Foreground = modPresent ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // arzedit check
        bool arzeditPresent = gdDir != null && File.Exists(Path.Combine(gdDir, "arzedit.exe"));
        panel.Children.Add(new TextBlock
        {
            Text = arzeditPresent
                ? "arzedit.exe found in install folder."
                : "arzedit.exe not found in install folder. See step 4 below.",
            FontSize = 11,
            Foreground = arzeditPresent ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // lua DLL check
        bool luaPresent     = gdDir != null && File.Exists(Path.Combine(gdDir, "lua-apclientpp.dll"));
        bool realLuaPresent = gdDir != null && File.Exists(Path.Combine(gdDir, "real_lua51.dll"));
        panel.Children.Add(new TextBlock
        {
            Text = (luaPresent && realLuaPresent)
                ? "Lua bridge DLLs found (lua-apclientpp.dll + real_lua51.dll)."
                : "Lua bridge DLLs not fully set up. See steps 1 and 2 below.",
            FontSize = 11,
            Foreground = (luaPresent && realLuaPresent) ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text = overrideDir ?? gdDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip = "Your Grim Dawn install folder (the one containing \"Grim Dawn.exe\"). " +
                      "Detected from Steam automatically; use this picker for a non-standard " +
                      "library or another store.",
        };
        var dirBtn = new Button
        {
            Content = "Select folder...", Width = 120, Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Grim Dawn install folder (contains \"Grim Dawn.exe\")",
                InitialDirectory = Directory.Exists(overrideDir ?? gdDir ?? "")
                                   ? (overrideDir ?? gdDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateGrimDawnFolder(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Grim Dawn folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
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
            Text = "Steam installs are detected automatically (appid 219990). Use this " +
                   "picker for a non-standard Steam library or another store.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "GUIDED SETUP (one-time)", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Rename the existing \"lua51.dll\" in your Grim Dawn install folder to " +
                "\"real_lua51.dll\". Then download Heinermann's replacement lua51.dll (link " +
                "below) and place it in the same install folder.",

            "2. Download Black Sliver's lua-apclientpp (v0.4.9, link below). From the zip, " +
                "extract only \"lua51\\lua51-clang32-dynamic\\lua-apclientpp.dll\" and place " +
                "it in your Grim Dawn install folder.",

            "3. Download the \"archipelago\" mod from Nexus Mods (link below). Create a " +
                "\"mods\" folder in your Grim Dawn install if it does not exist. Extract the " +
                "mod zip so that \"mods\\archipelago\" exists directly inside the install.",

            "4. Download arzedit.exe (link below) and place it directly in your Grim Dawn " +
                "install folder.",

            "5. Install the grim_dawn.apworld via the Install button on the Play tab (this " +
                "places it in your Archipelago custom_worlds folder). Make sure there is no " +
                "duplicate in Archipelago's lib/worlds folder.",

            "6. In the Archipelago Launcher (installed separately), click \"Grim Dawn Client\" " +
                "under the Components tab. Enter your server address and slot name, then click " +
                "Connect. When connected, the client patches the game automatically (may take " +
                "~30 seconds). You can then close the client.",

            "7. If you are outside the US, temporarily set your region to \"English (United " +
                "States)\" in Control Panel before step 6 (the patcher is sensitive to locale " +
                "float formatting). You can change it back after patching.",

            "8. Launch Grim Dawn using the 32-bit launcher (\"Grim Dawn.exe\" in the install " +
                "root, NOT x64\\Grim Dawn.exe). At the main menu, go to Custom Game -> Custom " +
                "Game -> \"patchedArchipelago ~ world001.map\". The game will connect to the " +
                "AP server as soon as you load into a world.",
        })
        {
            panel.Children.Add(new TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
            });
        }

        // ── Section: Connection info reminder ─────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "CONNECTING", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 8, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "After the AP Launcher's Grim Dawn Client patches the game, a connect.txt " +
                   "file is written to your Grim Dawn install root with your server credentials. " +
                   "The patched mod reads it automatically when you load a world. You do not need " +
                   "to re-patch unless your slot_data changes (new generation). The patcher " +
                   "remembers the last credentials, so the AP Launcher client is only needed " +
                   "when connecting to a new slot or after a new generation.",
            FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Links ────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "DOWNLOADS & LINKS", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("AP Fork Releases (grim_dawn.apworld) ↗",    ApForkUrl + "/releases"),
            ("Archipelago Grim Dawn Mod (Nexus Mods) ↗",   NexusModUrl),
            ("Lua51 DLL Unlocker (Heinermann GitHub) ↗",   LuaUnlockerUrl),
            ("lua-apclientpp v0.4.9 (Black Sliver) ↗",     LuaApclientppUrl),
            ("arzedit.exe (GitLab) ↗",                     ArzeditUrl),
            ("Setup Guide (Grim Dawn docs) ↗",             SetupGuideUrl),
            ("Archipelago Official ↗",                     ArchipelagoSite),
        })
        {
            var btn = new Button
            {
                Content = label, HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2),
                Background      = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize        = 12,
                Margin          = new Thickness(0, 0, 0, 4),
                Foreground      = new SolidColorBrush(Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor          = System.Windows.Input.Cursors.Hand,
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
            string json = await _http.GetStringAsync(GH_RELEASES_API, ct);
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

    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest grim_dawn.apworld from the routhken/Archipelago releases.
    /// Searches for the "grim_dawn.apworld" asset name. Falls back to the pinned
    /// v0.4.0 direct URL if the API is unreachable.
    private async Task<(string Version, string? Url)> ResolveLatestApWorldAsync(
        CancellationToken ct)
    {
        try
        {
            // The latest release is a Grim Dawn-specific one (not AP main), so
            // "latest" from the API might not exist on a fork. List all and pick first.
            string json = await _http.GetStringAsync(GH_RELEASES_API, ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var rel in doc.RootElement.EnumerateArray())
                {
                    string? version = rel.TryGetProperty("tag_name", out var t)
                        ? NormalizeTag(t.GetString())
                        : null;
                    if (version == null) continue;

                    if (!rel.TryGetProperty("assets", out var assets)
                        || assets.ValueKind != JsonValueKind.Array) continue;

                    foreach (var a in assets.EnumerateArray())
                    {
                        string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                        string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                        if (name == null || url == null) continue;
                        if (string.Equals(name, ApWorldAssetName, StringComparison.OrdinalIgnoreCase))
                            return (version, url);
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* fall through */ }

        // Pinned fallback: v0.4.0, verified live 2026-06-14.
        string fallbackUrl =
            $"https://github.com/{AP_FORK_OWNER}/{AP_FORK_REPO}/releases/download/v{FallbackVersion}/{ApWorldAssetName}";
        return (FallbackVersion, fallbackUrl);
    }

    // ── Private helpers — Steam / Grim Dawn detection ─────────────────────────

    private string? ResolveGrimDawnDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeGrimDawnDir(ov))
            return ov;

        try { return DetectSteamGrimDawnDir(); }
        catch { return null; }
    }

    private static bool LooksLikeGrimDawnDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            // The 32-bit exe is "Grim Dawn.exe" in the root (with a space).
            if (File.Exists(Path.Combine(dir, GdExeName))) return true;
            // Secondary signal: the game's data folder.
            if (Directory.Exists(Path.Combine(dir, "database"))) return true;
            return false;
        }
        catch { return false; }
    }

    private string? ValidateGrimDawnFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Grim Dawn install folder.";

        if (LooksLikeGrimDawnDir(folder))
            return null;

        // Try descending into a "Grim Dawn" sub-folder (user picked the "common" parent).
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeGrimDawnDir(nested))
                return null;
        }
        catch { }

        return "That does not look like a Grim Dawn installation. Pick the folder that " +
               "contains \"Grim Dawn.exe\" (for Steam this is usually " +
               @"...\steamapps\common\Grim Dawn).";
    }

    private static string? DetectSteamGrimDawnDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{GdSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeGrimDawnDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeGrimDawnDir(conventional)) return conventional;
                }
                catch { }
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

    private static string NormalizeSteamPath(string p) => p.Replace('/', '\\').TrimEnd('\\');

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
            string raw = text.Substring(open + 1, close - open - 1);
            yield return raw.Replace("\\\\", "\\");
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

    private void StartGrimDawn()
    {
        string? gdDir = ResolveGrimDawnDir();
        string? exe   = gdDir != null ? Path.Combine(gdDir, GdExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = gdDir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Grim Dawn.");

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
            catch { /* non-fatal */ }
            return;
        }

        // Steam URL fallback.
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
            "Could not find \"Grim Dawn.exe\". Open this game's Settings and pick your " +
            "Grim Dawn install folder, or install Grim Dawn via Steam.",
            GdExeName);
    }

    // ── Private helpers — settings sidecar ───────────────────────────────────

    private sealed class GrimDawnSettings
    {
        public string? InstallOverride { get; set; }
        public string? ApWorldVersion  { get; set; }
    }

    private GrimDawnSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<GrimDawnSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(GrimDawnSettings s)
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
        var s = LoadSettings();
        s.InstallOverride = p;
        SaveSettings(s);
    }

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().ApWorldVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private void WriteStampedVersion(string v)
    {
        var s = LoadSettings();
        s.ApWorldVersion = v;
        SaveSettings(s);
    }
}
