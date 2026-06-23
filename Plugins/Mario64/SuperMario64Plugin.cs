using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Mario64;

// ═══════════════════════════════════════════════════════════════════════════════
// SuperMario64Plugin — install-helper / launch for "Super Mario 64" played through
// sm64ex (the sm64-port / sm64ex PC decompilation port) with the Archipelago
// patch. This is a NATIVE "ConnectsItself" integration (NOT a BizHawk / Lua
// emulator game): the modded PC port speaks to the AP server itself, exactly like
// Ship of Harkinian, APDOOM and the OpenTTD Archipelago fork.
//
// Modelled on Plugins/Doom/Doom1993Plugin.cs (native-PC, self-contained JSON
// sidecar) and Plugins/SoH/SoHPlugin.cs (build-from-the-user's-own-asset honesty).
//
// ─────────────────────────────────────────────────────────────────────────────
// REALITY CHECK (2026-06-14) — facts verified online + against the vendored
// AP-main checkout (mk64src/worlds/sm64ex) this session. This is the HONEST
// install ceiling and it is fundamentally different from SoH/APDOOM:
//
//   THERE IS NO PREBUILT ARCHIPELAGO GAME EXE TO DOWNLOAD.
//
// sm64ex is a decompilation port: the original Super Mario 64 assets (graphics,
// audio, level data) are baked into the binary AT COMPILE TIME, extracted from
// the user's OWN US/JP SM64 ROM (named `baserom.us.z64` / `baserom.jp.z64` in the
// source tree). Shipping a prebuilt exe with those assets baked in would be
// piracy, so NO upstream distributes a ready-to-run AP game binary. The verified
// Archipelago distribution is one of:
//
//   * SM64AP-Launcher  (https://github.com/N00byKing/SM64AP-Launcher) — a Qt
//     build/management TOOL. On Windows it drives an MSYS2 toolchain, clones the
//     AP sm64ex source (N00byKing/sm64ex @ branch "archipelago"), and COMPILES a
//     build from the user's ROM. Latest release "rel8" (2026-02-05); Windows
//     asset "SM64AP-Launcher_windows.zip" (verified via the GitHub releases API).
//   * Manual clone+build of N00byKing/sm64ex @ "archipelago" via MSYS2 + make.
//
// Either way the OUTPUT is a compiled exe at `<build>/build/us_pc/sm64.us.f3dex2e.exe`
// (JP: `sm64.jp.f3dex2e.exe`) that the user produces locally. (Source: the
// official Archipelago "Super Mario 64 EX MultiWorld Setup Guide",
// mk64src/worlds/sm64ex/docs/setup_en.md.)
//
// WHAT THIS PLUGIN HONESTLY DOES (no fabricated one-click that cannot exist):
//   1. "Install Game" downloads + extracts the SM64AP-Launcher BUILD TOOL (the
//      one legally-distributable artifact) into the install folder, so the user
//      has the official compiler one click away. It does NOT — and cannot —
//      produce a runnable game by itself; the post-install message says so and
//      points at the numbered setup steps. This mirrors SoH/Jak honesty: automate
//      what's legal, guide the irreducible (ROM + compile) parts.
//   2. Bring-your-own ROM (§11): the user points at their SM64 US ROM; the
//      launcher validates it by CONTENT (z64 ≈ 8,388,608 B, loose 6–10 MB
//      window), copies it into Games/ROMs/sm64ex/ (original NEVER modified), and
//      stages a copy named `baserom.us.z64` next to the build tool so the
//      compile step finds it without prompting.
//   3. "Play" LOCATES the user's already-compiled `sm64.us.f3dex2e.exe` (in the
//      install tree, or a folder they pick in Settings) and launches it with the
//      VERIFIED AP command-line args (below). If no compiled exe exists yet, it
//      shows the honest "build it first" guidance instead of pretending.
//   4. ConnectsItself = true — the port owns the slot connection; the launcher
//      holds no ApClient on the same slot while it runs. SupportsStandalone =
//      true — plain sm64ex runs without AP.
//
// HOW IT CONNECTS (VERIFIED against the official setup guide, "Joining a
// MultiWorld Game"): the port is launched with COMMAND-LINE arguments —
//       sm64.us.f3dex2e.exe --sm64ap_name <slot> --sm64ap_ip <host:port>
//       [--sm64ap_passwd "<pw>"]
// (Offline single-player instead uses --sm64ap_file "<path.apsm64ex>".) Names /
// passwords with spaces are quoted. AP allows one connection per slot, so — like
// SoH/APDOOM — ConnectsItself = true and credentials are passed on the command
// line (nothing plaintext written to disk).
//
// THE AP WORLD:
//   game string "Super Mario 64" — VERIFIED: mk64src/worlds/sm64ex/__init__.py →
//   SM64World.game = "Super Mario 64". The world is bundled with Archipelago
//   itself; the offline patch file extension is ".apsm64ex" (same file).
//
// DEFENSIVE / UNVERIFIED DETAILS (flagged, SoH/APDOOM-style):
//   * The compiled GAME exe name "sm64.us.f3dex2e.exe" is verified from the setup
//     guide, but a custom build (different makeflags/branch) can land elsewhere.
//     ResolveGameExe() therefore searches the install tree AND the user-set build
//     folder for sm64.*.f3dex2e.exe / any "sm64*.exe" that is NOT the launcher
//     tool, recursively. If only the build TOOL is found, Play shows guidance.
//   * The SM64AP-Launcher Windows zip name "SM64AP-Launcher_windows.zip" is
//     verified at rel8, but the resolver matches by pattern (win/windows .zip,
//     excluding linux/flatpak/appimage/source) with the pinned rel8 URL as the
//     offline fallback, so a future rename still resolves.
//   * One launcher-side setting set (ROM path + the user's compiled-build folder)
//     lives in this plugin's OWN JSON sidecar
//     (Games/ROMs/sm64ex/sm64_launcher.json), NOT Core/SettingsStore — this
//     plugin is added as a single self-contained file and deliberately does not
//     modify shared launcher types (same structural choice as Doom1993Plugin).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SuperMario64Plugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    // The official Archipelago build TOOL (the one legally-distributable
    // artifact). NOT a prebuilt game — it compiles sm64ex from the user's ROM.
    private const string GITHUB_OWNER = "N00byKing";
    private const string GITHUB_REPO  = "SM64AP-Launcher";
    private const string RepoUrl      = $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases";

    // The AP sm64ex source (built by the tool, or cloned manually). Linked for
    // the user; never downloaded as a binary by this plugin.
    private const string SourceRepoUrl = "https://github.com/N00byKing/sm64ex";

    private const string SetupGuideUrl =
        "https://archipelago.gg/tutorial/Super%20Mario%2064/setup/en";

    // Pinned fallback used only when the GitHub API is unreachable. "rel8"
    // (2026-02-05), Windows asset verified via the releases API this session.
    private const string FallbackVersion = "rel8";
    private const string FallbackZipName = "SM64AP-Launcher_windows.zip";
    private static readonly string FallbackZipUrl =
        $"{RepoUrl}/releases/download/{FallbackVersion}/{FallbackZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    /// Installed-version stamp (the BUILD TOOL's version), written after install.
    private const string VersionFileName = "sm64ap_launcher_version.dat";

    /// Canonical filename sm64ex's build step expects for the US ROM, staged next
    /// to the build tool so the compile finds it. (JP would be baserom.jp.z64 —
    /// the setup guide notes EU/Shindou are unsupported; we target the US dump,
    /// the common case, and never rename the user's original.)
    private const string StagedRomName = "baserom.us.z64";

    /// The verified compiled GAME exe name (US build). Resolution falls back to a
    /// fuzzy search for custom builds.
    private const string CompiledExeName = "sm64.us.f3dex2e.exe";

    /// SM64 US/JP ROM accepted by sm64ex's asset extractor. The N64 cartridge dump
    /// is 8 MB (8,388,608 B). We accept .z64/.n64/.v64 by extension and a loose
    /// 6–10 MB size window (do NOT gatekeep on a single MD5 — sm64ex's own build
    /// step is the authoritative validator, and several valid US/JP masters exist).
    private static readonly string[] RomExtensions = { ".z64", ".n64", ".v64" };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "sm64ex";
    public string DisplayName => "Super Mario 64";
    public string Subtitle    => "Native PC · built-in Archipelago";

    /// EXACT AP game string — VERIFIED against worlds/sm64ex/__init__.py
    /// (SM64World.game = "Super Mario 64").
    public string ApWorldName => "Super Mario 64";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "sm64ex.png");

    public string ThemeAccentColor => "#E11C1C";   // Mario red
    public string[] GameBadges     => new[] { "Requires SM64 ROM" };

    public string Description =>
        "Super Mario 64 played through sm64ex — a native PC port built from a " +
        "decompilation of the original game, with a built-in Archipelago client. " +
        "Power Stars, keys, caps and cannons are shuffled into the multiworld, and " +
        "the port connects to the Archipelago server itself — no emulator, no Lua " +
        "bridge. IMPORTANT: sm64ex ships no Nintendo data. It is COMPILED from your " +
        "own Super Mario 64 US ROM (the game's assets are extracted at build time), " +
        "so you supply your ROM and build the port once with the official " +
        "SM64AP-Launcher. The launcher fetches that build tool, stages your ROM for " +
        "it, and then launches and connects your compiled build.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    // "Installed" means the user has a COMPILED, runnable game exe (the whole
    // point — the build tool alone is not a playable game).
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }
    public bool    IsInstalled      => ResolveGameExe() != null;
    public bool    IsRunning        { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Root folder where the SM64AP build TOOL is installed (and where the user
    /// may also compile their build).
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "SuperMario64");

    private string VersionFilePath => Path.Combine(GameDirectory, VersionFileName);

    /// The launcher's own ROM-library copy of the user's SM64 ROM (§11).
    private string RomLibraryDirectory
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);

    /// Where the ROM must live for sm64ex's build step to extract assets — staged
    /// next to the build tool under the canonical name.
    private string StagedRomPath => Path.Combine(GameDirectory, StagedRomName);

    /// This plugin's OWN settings sidecar (see header — kept out of the shared
    /// SettingsStore so the plugin is one self-contained file). BOM-less UTF-8.
    private string SettingsSidecarPath
        => Path.Combine(RomLibraryDirectory, "sm64_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;
    private string?  _lastLaunchArgs;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // sm64ex's native AP client reports checks/items/goal to the AP server
    // itself — the launcher relays nothing. These exist for interface
    // compatibility (ConnectsItself = true).
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
            InstalledVersion = File.Exists(VersionFilePath)
                ? (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim()
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
                await GitHubHelper.FetchLatestTagAsync(GITHUB_OWNER, GITHUB_REPO, ct));
        }
        catch
        {
            AvailableVersion = null; // contract: never throw on network failure
        }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────
    // HONEST: this installs the official BUILD TOOL (the only legally-shippable
    // artifact). It cannot produce a playable game on its own — the user must run
    // the tool to compile their build from their ROM. The final message says so.

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 1. Resolve the latest build-tool release (pinned fallback when offline).
        progress.Report((2, "Checking latest SM64AP-Launcher (build tool) release..."));
        var (version, zipUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        // 2. Already have this tool version? (idempotent fast path)
        if (File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100,
                $"SM64AP-Launcher {version} is present. Build your sm64ex from your " +
                "SM64 ROM with it (see Settings), then press Play."));
            return;
        }

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find a Windows download for the SM64AP-Launcher build " +
                "tool on the GitHub release page. Check your internet connection, " +
                "or download it manually from " + RepoUrl + "/releases.");

        // 3. Download + extract the build tool.
        await DownloadAndExtractToolAsync(zipUrl, version, progress, ct);

        // 4. Stage the user's ROM next to the tool if they already picked one, so
        //    the compile step finds it without prompting.
        StageRomForBuild();

        // 5. Stamp the installed tool version.
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        progress.Report((100,
            $"SM64AP-Launcher {version} installed. This is the official BUILD TOOL — " +
            "it compiles sm64ex from your own SM64 US ROM. Open Settings for the " +
            "numbered build steps; once you have built your game, press Play."));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;   // true only when a compiled game exe is found
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        string exe = ResolveGameExe()
            ?? throw new FileNotFoundException(
                "No compiled Super Mario 64 build was found. sm64ex must be built " +
                "from your own SM64 US ROM first: install the SM64AP-Launcher build " +
                "tool, pick your ROM in Settings, and compile a build (see the " +
                "numbered steps in Settings). Then point the launcher at your build " +
                "folder and press Play.",
                Path.Combine(GameDirectory, CompiledExeName));

        // VERIFIED connection path (setup guide → "Joining a MultiWorld Game"):
        //   --sm64ap_name <slot> --sm64ap_ip <host:port> [--sm64ap_passwd "<pw>"]
        // Best-effort; never blocks the launch.
        string args = BuildLaunchArguments(session);
        StartGameProcess(exe, args);
        return Task.CompletedTask;
    }

    /// sm64ex is a complete game — plain (non-AP) play is supported.
    public bool SupportsStandalone => true;

    /// sm64ex's native in-game AP client owns the slot connection (see header).
    /// The launcher must not connect its own ApClient to the same slot while the
    /// game runs.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        string exe = ResolveGameExe()
            ?? throw new FileNotFoundException(
                "No compiled Super Mario 64 build was found. Build sm64ex from your " +
                "own SM64 US ROM first (see Settings).",
                Path.Combine(GameDirectory, CompiledExeName));

        // No AP args — plain sm64ex (the player simply doesn't connect, or loads
        // an offline .apsm64ex patch manually).
        StartGameProcess(exe, "");
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // AP credentials are passed on the command line (not written to disk),
        // so there is no plaintext password file to scrub — but clear our cached
        // last-args defensively all the same.
        _lastLaunchArgs = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // sm64ex's native client receives items from the AP server directly;
        // there is nothing to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // sm64ex renders its own AP status in-game; no launcher HUD channel.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var warn    = new SolidColorBrush(Color.FromRgb(0xE0, 0xB0, 0x4C));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Honest "build it yourself" header (unverified-offline honesty) ──
        panel.Children.Add(new TextBlock
        {
            Text = "Super Mario 64 (sm64ex) is COMPILED from your own SM64 US ROM — " +
                   "there is no prebuilt download. The launcher installs the official " +
                   "build tool and stages your ROM; you build the game once, then the " +
                   "launcher runs and connects it. Steps below.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: Install / build-tool directory ───────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "INSTALL / BUILD DIRECTORY", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text = GameDirectory, IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var dirBtn = new Button
        {
            Content = "Browse...", Width = 90, Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select SM64AP-Launcher / build folder",
                InitialDirectory = Directory.Exists(GameDirectory) ? GameDirectory
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                GameDirectory = dlg.FolderName;
                dirBox.Text   = dlg.FolderName;
            }
        };
        DockPanel.SetDock(dirBtn, Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new TextBlock
        {
            Text       = IsInstalled
                ? "✓ A compiled Super Mario 64 build was found — press Play."
                : "No compiled game build found yet (install the build tool below, " +
                  "then build sm64ex from your ROM).",
            FontSize   = 11, Foreground = IsInstalled ? success : muted,
            TextWrapping = TextWrapping.Wrap,
            Margin     = new Thickness(0, 6, 0, 12),
        });

        // ── Section: compiled-build folder (where sm64.us.f3dex2e.exe lives) ─
        panel.Children.Add(new TextBlock
        {
            Text = "COMPILED BUILD FOLDER (optional)", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 8, 0, 8),
        });

        var buildRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var buildBox = new TextBox
        {
            Text = LoadBuildFolder() ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var buildBtn = new Button
        {
            Content = "Locate build...", Width = 120, Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        buildBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select the folder containing your compiled sm64.us.f3dex2e.exe (build/us_pc)",
                InitialDirectory = Directory.Exists(GameDirectory) ? GameDirectory
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                SaveBuildFolder(dlg.FolderName);
                buildBox.Text = dlg.FolderName;
            }
        };
        DockPanel.SetDock(buildBtn, Dock.Right);
        buildRow.Children.Add(buildBtn);
        buildRow.Children.Add(buildBox);
        panel.Children.Add(buildRow);

        panel.Children.Add(new TextBlock
        {
            Text = "If you built sm64ex somewhere else, point the launcher at the " +
                   "folder that holds sm64.us.f3dex2e.exe (normally <your build>\\build\\us_pc). " +
                   "Leave blank to auto-detect inside the install folder above.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Super Mario 64 ROM ───────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "SUPER MARIO 64 ROM (US)", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 8, 0, 8),
        });

        var romRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var romBox = new TextBox
        {
            Text = LoadRomPath() ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var romBtn = new Button
        {
            Content = "Select ROM...", Width = 110, Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        romBtn.Click += (_, _) =>
        {
            if (PromptForRomFile())
                romBox.Text = LoadRomPath() ?? "";
        };
        DockPanel.SetDock(romBtn, Dock.Right);
        romRow.Children.Add(romBtn);
        romRow.Children.Add(romBox);
        panel.Children.Add(romRow);

        panel.Children.Add(new TextBlock
        {
            Text = "sm64ex needs your own Super Mario 64 US ROM to extract game assets " +
                   "at build time. The launcher copies it into its own folder and stages " +
                   "a copy named baserom.us.z64 next to the build tool — your original " +
                   "file is never modified. (Europe/Shindou ROMs are not supported.)",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: numbered setup steps (the irreducible guided parts) ──
        panel.Children.Add(new TextBlock
        {
            Text = "HOW TO SET UP", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 8, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Press Install Game to download the official SM64AP-Launcher build tool.",
            "2. Select your Super Mario 64 US ROM above (it is staged as baserom.us.z64).",
            "3. On Windows, install MSYS2 from msys2.org into a path WITHOUT spaces " +
                "(the build tool guides this via its \"Check Requirements\" button).",
            "4. In the SM64AP-Launcher, use \"Compile default SM64AP build\" (or a " +
                "custom build) to compile sm64ex from your ROM — this takes a few minutes.",
            "5. Back here, leave the build folder blank to auto-detect, or use " +
                "\"Locate build...\" to point at the folder with sm64.us.f3dex2e.exe.",
            "6. Press Play — the launcher runs your build and connects it to the room " +
                "(--sm64ap_name / --sm64ap_ip / --sm64ap_passwd are passed for you).",
        })
        {
            panel.Children.Add(new TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 12, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("SM64AP-Launcher (GitHub) ↗",   RepoUrl),
            ("sm64ex AP source (GitHub) ↗",  SourceRepoUrl),
            ("Super Mario 64 Setup Guide ↗", SetupGuideUrl),
            ("Archipelago Official ↗",       "https://archipelago.gg"),
        })
        {
            var btn = new Button
            {
                Content = label, HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), FontSize = 12, Margin = new Thickness(0, 0, 0, 4),
                Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) => { try { System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(u) { UseShellExecute = true }); } catch { } };
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
            using var doc  = JsonDocument.Parse(json);
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

    /// "rel8" stays "rel8"; "v1.2" → "1.2". Returns null for null/blank tags.
    /// SM64AP-Launcher tags are not semver (e.g. "rel8"), so keep the raw tag
    /// unless a leading 'v' decorates a digit.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest build-tool release: version + Windows zip asset URL.
    /// Enumerates /releases (newest first; prereleases accepted) and picks the
    /// Windows zip by pattern. Falls back to the pinned rel8 direct URL when the
    /// API is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var rel in doc.RootElement.EnumerateArray())
                {
                    if (rel.TryGetProperty("draft", out var dr) &&
                        dr.ValueKind == JsonValueKind.True)
                        continue;

                    string? version = rel.TryGetProperty("tag_name", out var t)
                        ? NormalizeTag(t.GetString())
                        : null;
                    if (version == null) continue;

                    if (rel.TryGetProperty("assets", out var assets) &&
                        assets.ValueKind == JsonValueKind.Array)
                    {
                        string? zip = PickWindowsZip(assets);
                        if (zip != null)
                            return (version, zip);
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback below */ }

        return (FallbackVersion, FallbackZipUrl);
    }

    /// Pick the Windows .zip from a release's assets (by win/windows pattern,
    /// excluding linux/flatpak/appimage/source). Asset casing can vary, so match
    /// broadly; fall back to any non-Linux .zip.
    private static string? PickWindowsZip(JsonElement assets)
    {
        string? win = null, anyZip = null;
        foreach (var a in assets.EnumerateArray())
        {
            string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name == null || url == null) continue;

            string lower = name.ToLowerInvariant();
            if (!lower.EndsWith(".zip")) continue;
            if (lower.Contains("source")) continue;
            if (lower.Contains("linux") || lower.Contains("appimage") || lower.Contains("flatpak"))
                continue;

            anyZip ??= url;
            if (win == null && (lower.Contains("win") || lower.Contains("windows")))
                win = url;
        }
        return win ?? anyZip;
    }

    // ── Private helpers — exe resolution ──────────────────────────────────────

    /// Resolve the user's COMPILED game exe (NOT the build tool). Search order:
    ///   1. the user-set compiled-build folder (if any),
    ///   2. the install/build directory tree, recursively.
    /// Prefer the verified "sm64.us.f3dex2e.exe"; accept "sm64.jp.f3dex2e.exe"
    /// and any "sm64*.exe" that is clearly the game (in a us_pc/jp_pc folder or
    /// matching the f3dex pattern), while EXCLUDING the SM64AP-Launcher tool exe.
    private string? ResolveGameExe()
    {
        string? buildFolder = LoadBuildFolder();
        string? hit = buildFolder != null && Directory.Exists(buildFolder)
            ? ScanForGameExe(buildFolder)
            : null;
        if (hit != null) return hit;

        if (Directory.Exists(GameDirectory))
            return ScanForGameExe(GameDirectory);

        return null;
    }

    private static string? ScanForGameExe(string root)
    {
        try
        {
            // Fast path: the canonical US build in a us_pc folder.
            string direct = Path.Combine(root, CompiledExeName);
            if (File.Exists(direct)) return direct;

            string? jp = null, fuzzy = null;
            foreach (string exe in Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();

                // Never treat the build/management TOOL or installers as the game.
                if (name.Contains("launcher") || name.Contains("sm64ap-launcher") ||
                    name.Contains("unins") || name.Contains("setup"))
                    continue;

                if (name == "sm64.us.f3dex2e") return exe;     // exact verified US
                if (name == "sm64.jp.f3dex2e") jp ??= exe;      // verified JP build
                else if (name.StartsWith("sm64") && name.Contains("f3dex"))
                    fuzzy ??= exe;                              // custom f3dex build
                else if (name.StartsWith("sm64") &&
                         exe.Replace('\\', '/').Contains("/us_pc/"))
                    fuzzy ??= exe;                              // sm64* in us_pc
            }
            return jp ?? fuzzy;
        }
        catch { /* directory vanished mid-scan */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Build the VERIFIED AP launch command line (setup guide → "Joining a
    /// MultiWorld Game"):
    ///   --sm64ap_name <slot> --sm64ap_ip <host:port> [--sm64ap_passwd "<pw>"]
    /// Slot/password are quoted to survive spaces.
    private string BuildLaunchArguments(ApSession session)
    {
        var (host, port) = ParseServerHostPort(session.ServerUri);
        var sb = new StringBuilder();

        sb.Append("--sm64ap_name ").Append(Quote(session.SlotName));
        sb.Append(" --sm64ap_ip ").Append(Quote($"{host}:{port}"));
        if (!string.IsNullOrEmpty(session.Password))
            sb.Append(" --sm64ap_passwd ").Append(Quote(session.Password));

        return sb.ToString();
    }

    /// Quote an argument for a Windows command line (wrap in double quotes and
    /// escape embedded quotes). Plain tokens are returned unquoted.
    private static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        bool needs = value.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0;
        if (!needs) return value;
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    /// Accepts "archipelago.gg:38281", "ws://host:port", "wss://host:port", a
    /// bare hostname, and IPv6 literals (bracketed "[::1]:38281" or bare "::1").
    /// Default AP port is 38281.
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

    private void StartGameProcess(string exePath, string arguments)
    {
        _lastLaunchArgs = arguments;

        var psi = new ProcessStartInfo
        {
            FileName         = exePath,
            // sm64ex reads its save/config relative to the exe — run from there.
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? GameDirectory,
            UseShellExecute  = false,
        };
        if (!string.IsNullOrEmpty(arguments))
            psi.Arguments = arguments;

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start Super Mario 64 (sm64ex).");

        _gameProcess = proc;
        IsRunning    = true;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            IsRunning       = false;
            _lastLaunchArgs = null;
            GameExited?.Invoke(proc.ExitCode);
        };
    }

    // ── Private helpers — ROM (bring-your-own) ────────────────────────────────

    /// Open the ROM picker, validate by CONTENT (extension + plausible ~8 MB
    /// size), copy into the launcher's own ROM library (§11 — original never
    /// touched), persist the COPY's path, and stage it as baserom.us.z64 next to
    /// the build tool. Returns true when a ROM was imported.
    private bool PromptForRomFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Select your Super Mario 64 US ROM",
            Filter = "Nintendo 64 ROM (*.z64;*.n64;*.v64)|*.z64;*.n64;*.v64|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return false;

        string? bad = ValidateSm64Rom(dlg.FileName);
        if (bad != null)
        {
            MessageBox.Show(bad, "Not a valid Super Mario 64 ROM",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        try
        {
            Directory.CreateDirectory(RomLibraryDirectory);
            string dst = Path.Combine(RomLibraryDirectory, Path.GetFileName(dlg.FileName));
            File.Copy(dlg.FileName, dst, overwrite: true);
            SaveRomPath(dst);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not copy the ROM into the launcher library:\n{ex.Message}\n\n" +
                "Nothing was changed — your original file is untouched.",
                "ROM import failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        StageRomForBuild();
        return true;
    }

    /// Content check for an SM64 ROM: known extension + plausible size. An N64
    /// SM64 cartridge dump is 8 MB (8,388,608 B); accept a loose 6–10 MB window
    /// to allow byte-order variants and avoid gatekeeping on a single MD5 —
    /// sm64ex's build step is the authoritative validator. Returns null when
    /// acceptable, else a short human-readable reason.
    private static string? ValidateSm64Rom(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (Array.IndexOf(RomExtensions, ext) < 0)
            return "That file is not a Nintendo 64 ROM (expected .z64, .n64, or .v64).";

        try
        {
            long len = new FileInfo(path).Length;
            const long min = 6L  * 1024 * 1024;   // ~6 MB lower bound
            const long max = 10L * 1024 * 1024;   // ~10 MB upper bound
            if (len < min || len > max)
                return "That file is the wrong size for a Super Mario 64 ROM " +
                       "(expected about 8 MB). Make sure it is the full N64 cartridge dump.";
        }
        catch
        {
            return "Could not read that file. Pick a different ROM and try again.";
        }
        return null;
    }

    /// Copy the user's library ROM to baserom.us.z64 next to the build tool so
    /// sm64ex's compile step extracts assets without prompting. Best effort —
    /// never throws into a launch/install.
    private void StageRomForBuild()
    {
        try
        {
            string? lib = LoadRomPath();
            if (string.IsNullOrEmpty(lib) || !File.Exists(lib)) return;
            if (!Directory.Exists(GameDirectory)) return;

            // Only re-copy when missing or changed (cheap length compare).
            if (File.Exists(StagedRomPath))
            {
                try
                {
                    if (new FileInfo(StagedRomPath).Length == new FileInfo(lib).Length)
                        return;
                }
                catch { /* fall through and re-copy */ }
            }
            File.Copy(lib, StagedRomPath, overwrite: true);
        }
        catch { /* staging is a convenience — the build tool can also locate a ROM */ }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // This plugin keeps its launcher-side settings (ROM path + compiled-build
    // folder) in its OWN JSON file so it stays a single self-contained source
    // file and does not modify Core/SettingsStore. BOM-less UTF-8, read-modify-
    // write.

    private sealed class Sm64Settings
    {
        public string? RomPath     { get; set; }
        public string? BuildFolder { get; set; }
    }

    private Sm64Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<Sm64Settings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(Sm64Settings s)
    {
        try
        {
            Directory.CreateDirectory(RomLibraryDirectory);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal — settings just won't persist this time */ }
    }

    private string? LoadRomPath()           => LoadSettings().RomPath;
    private void    SaveRomPath(string p)    { var s = LoadSettings(); s.RomPath = p;     SaveSettings(s); }
    private string? LoadBuildFolder()        => LoadSettings().BuildFolder;
    private void    SaveBuildFolder(string p){ var s = LoadSettings(); s.BuildFolder = p; SaveSettings(s); }

    // ── Private helpers — download/extract (the build TOOL) ───────────────────

    private async Task DownloadAndExtractToolAsync(
        string zipUrl,
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"sm64ap-launcher-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((5, $"Downloading SM64AP-Launcher {version}..."));
            using var response = await _http.GetAsync(
                zipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long total      = response.Content.Headers.ContentLength ?? -1;
            long downloaded = 0;

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
                        int pct = (int)(5 + 70 * downloaded / total);
                        progress.Report((pct, $"Downloading SM64AP-Launcher... {downloaded / 1_000_000}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((80, "Extracting build tool..."));
            Directory.CreateDirectory(GameDirectory);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, GameDirectory, overwriteFiles: true);

            progress.Report((90, "Build tool extracted."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }
}
