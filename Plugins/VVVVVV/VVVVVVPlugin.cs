using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core;

namespace LauncherV2.Plugins.VVVVVV;

// ═══════════════════════════════════════════════════════════════════════════════
// VVVVVVPlugin — install / update / launch for "VVVVVV" played through its
// Archipelago fork ("V6AP"). This is a NATIVE "ConnectsItself" integration in the
// same family as Ship of Harkinian, Celeste 64, OpenTTD and APDOOM: the game
// speaks to the AP server itself via a built-in C++ client (APCpp.dll) — no
// emulator, no Lua bridge, and no launcher-held ApClient on the slot.
//
// REALITY CHECK (2026-06-14) — every fact below was verified online this session
// ─────────────────────────────────────────────────────────────────────────────
//   * THE AP WORLD: game string "VVVVVV" — verified against AP-main
//     worlds/v6/__init__.py: `class V6World(World)` with `game: str = "VVVVVV"`
//     (line 29 of the current main checkout). World id "v6". This is a CORE
//     Archipelago world — it ships INSIDE Archipelago itself, so there is NO
//     apworld to download and NOTHING to drop into custom_worlds (unlike SoH /
//     Celeste 64). The plugin therefore does not fetch or stage any apworld.
//
//   * THE AP FORK / REPO (verified online + the official Archipelago "VVVVVV
//     MultiWorld Setup Guide", https://archipelago.gg/tutorial/VVVVVV/setup_en):
//       N00byKing/VVVVVV — "V6AP", a fork of the open-source TerryCavanagh/VVVVVV
//       engine with a built-in Archipelago client.
//     Releases are NORMAL (non-prerelease) GitHub releases, so /releases/latest
//     works. Latest at time of writing: tag "AP0.5.1-3"
//     ("V6AP for Archipelago 0.5.1-3 (Maintenance Release)"). The Windows asset
//     name is "V6AP-win.zip" — verified IDENTICAL across EVERY release on the
//     repo (AP0.2.5 … AP0.5.1-3), so it is treated as a known fixed name (with a
//     broad pattern fallback). AP0.5.1-3 is pinned as the offline fallback so a
//     fresh install still works when the GitHub API is unreachable.
//
//   * WHAT THE DOWNLOAD CONTAINS (VERIFIED by inspecting the real zip this
//     session): EXACTLY TWO files — "VVVVVV.exe" (the patched game, ~2.5 MB) and
//     "APCpp.dll" (the Archipelago C++ client, ~1 MB). There is NO "data.zip"
//     (VVVVVV's graphics/audio asset bundle) in the download. This matches the
//     setup guide's instruction "Unpack the zip file where you have VVVVVV
//     installed": V6AP REPLACES the engine exe but reuses the COMMERCIAL game's
//     assets. The TerryCavanagh/VVVVVV engine source is open, but the GRAPHICS,
//     AUDIO AND LEVEL DATA are NOT — they live in "data.zip" and ship only with
//     the paid Steam/GOG build. So unlike Celeste 64 (free + open-content), this
//     is a BRING-YOUR-OWN-ASSET case: the user must own VVVVVV (Steam appid
//     70300 / GOG) and supply their "data.zip".
//
//   * HOW IT CONNECTS (VERIFIED, verbatim, against the setup guide): V6AP reads
//     the Archipelago connection from COMMAND-LINE LAUNCH OPTIONS — there is no
//     config file and no in-game menu. The documented options are:
//         -v6ap_name slotName            (mandatory)
//         -v6ap_ip   server:port         (mandatory)
//         -v6ap_passwd secretPassword    (only if the room has a password)
//         -v6ap_file filePath            (offline single-player / spoiler file)
//     "if there are spaces in your slot name or password, it should be surrounded
//     with quotes". This plugin passes these as process arguments on launch — so
//     unlike SoH's CVar guesswork, the connection prefill here is VERIFIED, not
//     defensive. Because the password is passed as a process argument (never
//     written to disk), there is no on-disk password to scrub — StopAsync simply
//     drops the in-memory copy with the process. AP servers allow one connection
//     per slot, so — like SoH / Celeste 64 / OpenTTD — the launcher must NOT hold
//     its own ApClient on the same slot while the game runs: ConnectsItself=true.
//
// WHAT THIS PLUGIN DOES (V2.0)
// ────────────────────────────
//   1. Installs/updates V6AP from N00byKing/VVVVVV GitHub releases (latest tag,
//      pinned AP0.5.1-3 fallback when the API is unreachable). The download is the
//      patched VVVVVV.exe + APCpp.dll only.
//   2. Lets the user point at their own VVVVVV "data.zip"; the launcher validates
//      it by CONTENT (zip with VVVVVV's known graphics entries), copies it into
//      its OWN Games/ROMs/<GameId>/ library AND stages a copy next to VVVVVV.exe
//      so the patched engine finds it. The user's original file is never modified
//      (§11). IsInstalled requires BOTH the exe and a staged data.zip.
//   3. On AP launch, passes the session's slot / host:port / password as the
//      VERIFIED -v6ap_* launch options, then runs VVVVVV.exe. Plain non-AP play
//      works identically (SupportsStandalone = true) — just no -v6ap_* options.
//   4. ConnectsItself semantics — the launcher tracks process lifetime only, no
//      pipes, no Lua. The AP server side sees the slot connect.
//
// DEFENSIVE / UNVERIFIED DETAILS (flagged, "verify at build time"):
//   * EXE NAME is "VVVVVV.exe" (verified from the real zip). ResolveGameExe()
//     prefers it, then falls back to any "*vvvvvv*"/"*v6*" exe in the install
//     (skipping helper/uninstaller exes) and flattens a single wrapper sub-folder.
//   * The exact internal layout of "data.zip" can vary by store/version, so the
//     content check is LENIENT: a valid zip that contains VVVVVV's signature
//     graphics entries (e.g. a "graphics/" folder or "*.png" sheets like
//     "tiles.png"/"sprites.png"), OR — as a last resort — any plausibly-sized zip
//     literally named "data.zip". This validates the file is the asset bundle
//     without gatekeeping on a single byte-exact master across store variants.
//   * One launcher-side setting (the data.zip library path) is kept in this
//     plugin's OWN JSON sidecar (Games/ROMs/v6/v6_launcher.json) rather than
//     touching the shared Core/SettingsStore — same self-contained approach as
//     Doom1993Plugin / Celeste64Plugin / StardewValleyPlugin.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class VVVVVVPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GITHUB_OWNER = "N00byKing";
    private const string GITHUB_REPO  = "VVVVVV";
    private const string RepoUrl      = $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases";
    private const string GH_RELEASES_LATEST_URL = $"{GH_RELEASES_URL}/latest";

    /// Official Archipelago "VVVVVV" setup guide.
    private const string SetupGuideUrl = "https://archipelago.gg/tutorial/VVVVVV/setup_en";
    private const string SteamStoreUrl = "https://store.steampowered.com/app/70300/VVVVVV/";
    private const string GogStoreUrl   = "https://www.gog.com/game/vvvvvv";

    // Pinned fallback — the latest non-prerelease at time of writing, with the
    // verified asset name. Used ONLY when the GitHub API is unreachable so a fresh
    // install still works offline-of-the-API.
    private const string FallbackVersion = "AP0.5.1-3";
    private const string FallbackZipName = "V6AP-win.zip";
    private static readonly string FallbackZipUrl =
        $"{RepoUrl}/releases/download/{FallbackVersion}/{FallbackZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    /// Installed-version stamp, written after a successful install.
    private const string VersionFileName = "v6ap_version.dat";

    /// VVVVVV's commercial asset bundle, supplied by the user from their Steam/GOG
    /// copy. The patched engine reads it from next to VVVVVV.exe.
    private const string DataFileName = "data.zip";

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "v6";
    public string DisplayName => "VVVVVV";
    public string Subtitle    => "Native PC · built-in Archipelago";

    /// EXACT AP game string — verified against worlds/v6/__init__.py
    /// (V6World.game = "VVVVVV"). VVVVVV is a CORE Archipelago world.
    public string ApWorldName => "VVVVVV";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "v6.png");

    public string ThemeAccentColor => "#264F9E";   // VVVVVV navy/Viridian blue
    public string[] GameBadges     => new[] { "Requires VVVVVV (data.zip)" };

    public string Description =>
        "VVVVVV is Terry Cavanagh's gravity-flipping platformer: you can't jump, " +
        "but you can invert gravity to walk on the ceiling and navigate the lost " +
        "dimension. This is the Archipelago build (\"V6AP\"), a fork of the " +
        "open-source VVVVVV engine with a built-in multiworld client — the game " +
        "connects to the Archipelago server itself, no emulator and no Lua bridge. " +
        "The engine source is open, but VVVVVV's graphics and audio are not: the " +
        "download is just the patched executable, so you need to own VVVVVV on " +
        "Steam or GOG and provide its data.zip. Point the launcher at your " +
        "data.zip once, and pressing Play connects you straight into the multiworld.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// Ready to play only when BOTH the patched exe and the user's data.zip are
    /// present next to it (V6AP without data.zip cannot start).
    public bool IsInstalled => ResolveGameExe() != null && File.Exists(StagedDataPath);
    public bool IsRunning   { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Root folder where the V6AP build is installed (patched exe + APCpp.dll +
    /// the staged data.zip).
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "VVVVVV");

    /// Preferred exe (verified name). Resolution falls back to a fuzzy match.
    private string PreferredExePath => Path.Combine(GameDirectory, "VVVVVV.exe");

    /// The user's data.zip staged next to VVVVVV.exe so the engine finds it.
    private string StagedDataPath => Path.Combine(GameDirectory, DataFileName);

    private string VersionFilePath => Path.Combine(GameDirectory, VersionFileName);

    /// The launcher's own library copy of the user's data.zip (§11 — the original
    /// is never touched). Kept under the ROM-library tree for consistency with the
    /// other native plugins, even though this asset is not a ROM.
    private string DataLibraryDirectory
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);

    private string DataLibraryPath => Path.Combine(DataLibraryDirectory, DataFileName);

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained source file).
    private string SettingsSidecarPath
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId,
                        "v6_launcher.json");

    private string SettingsSidecarDir => Path.GetDirectoryName(SettingsSidecarPath)!;

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // V6AP's native AP client reports checks/items/goal to the AP server itself —
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
            InstalledVersion = File.Exists(VersionFilePath) && ResolveGameExe() != null
                ? (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim()
                : null;
        }
        catch
        {
            InstalledVersion = null;
        }

        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            AvailableVersion = doc.RootElement.TryGetProperty("tag_name", out var t)
                ? t.GetString()
                : null;
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
        // 1. Resolve the latest release (pinned fallback when offline).
        progress.Report((2, "Checking latest VVVVVV (V6AP) release..."));
        var (version, zipUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        // 2. Already current? (idempotent fast path)
        if (ResolveGameExe() != null
            && File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == version)
        {
            InstalledVersion = version;

            // Engine present and current — make sure the user's data.zip is staged
            // (it may have been imported after the engine, or the install moved).
            EnsureDataStaged();

            progress.Report((100, File.Exists(StagedDataPath)
                ? $"VVVVVV (V6AP) {version} is up to date."
                : $"VVVVVV (V6AP) {version} is installed — now point the launcher at " +
                  "your VVVVVV data.zip (Settings) to finish setup."));
            return;
        }

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find a Windows download for VVVVVV (V6AP) on the GitHub " +
                "release page. Check your internet connection, or download the build " +
                "manually from " + RepoUrl + "/releases.");

        // 3. Download + extract the patched engine (exe + APCpp.dll only).
        await DownloadAndExtractGameAsync(zipUrl, version, progress, ct);

        // 4. Stage the user's data.zip next to the exe if they have already
        //    imported one (best effort — IsInstalled stays false until present).
        progress.Report((90, "Staging VVVVVV assets..."));
        EnsureDataStaged();

        // 5. Stamp the installed version.
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        progress.Report((100, File.Exists(StagedDataPath)
            ? $"VVVVVV (V6AP) {version} ready. Press Play to connect — the launcher " +
              "fills in your Archipelago connection automatically."
            : $"VVVVVV (V6AP) {version} installed. One more step: open Settings and " +
              "point the launcher at your VVVVVV data.zip (from your Steam/GOG copy)."));
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
        string exe = ResolveGameExe()
            ?? throw new FileNotFoundException(
                "VVVVVV (V6AP) is not installed. Click Install Game first.",
                PreferredExePath);

        // Make sure the user's data.zip is staged next to the exe.
        EnsureDataStaged();
        if (!File.Exists(StagedDataPath))
            throw new FileNotFoundException(
                "VVVVVV needs its data.zip (graphics/audio) from your own Steam or GOG " +
                "copy. Open Settings and point the launcher at your data.zip, then press " +
                "Play again. Your original file is never modified.",
                StagedDataPath);

        // VERIFIED connection path: pass the documented -v6ap_* launch options as
        // process arguments (slot / host:port / password). No config file, no
        // in-game menu — this is exactly what the setup guide documents.
        string args = BuildApLaunchArgs(session);
        StartGameProcess(exe, args);
        return Task.CompletedTask;
    }

    /// VVVVVV is a complete game — plain (non-AP) play is supported.
    public bool SupportsStandalone => true;

    /// V6AP's native in-game AP client owns the slot connection (see header). The
    /// launcher must not connect its own ApClient to the same slot while the game
    /// runs.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        string exe = ResolveGameExe()
            ?? throw new FileNotFoundException(
                "VVVVVV (V6AP) is not installed. Click Install Game first.",
                PreferredExePath);

        EnsureDataStaged();
        if (!File.Exists(StagedDataPath))
            throw new FileNotFoundException(
                "VVVVVV needs its data.zip (graphics/audio) from your own Steam or GOG " +
                "copy. Open Settings and point the launcher at your data.zip first.",
                StagedDataPath);

        // No -v6ap_* options — plain VVVVVV (the V6AP build runs as the normal game
        // when no Archipelago launch options are present).
        StartGameProcess(exe, "");
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // The room password was passed as a process ARGUMENT, never written to
        // disk by this plugin — so there is nothing on disk to scrub. The
        // in-memory copy dies with the process / this call.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // V6AP's native client receives items from the AP server directly; there
        // is nothing to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // V6AP renders its own AP status in-game; no launcher HUD channel.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xE0, 0xA8, 0x3A));
        var panel   = new StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Section: Install directory ────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "INSTALL DIRECTORY", FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text = GameDirectory, IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var dirBtn = new Button
        {
            Content = "Browse...", Width = 90, Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select VVVVVV (V6AP) install folder",
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

        bool exePresent  = ResolveGameExe() != null;
        bool dataPresent = File.Exists(StagedDataPath);
        panel.Children.Add(new TextBlock
        {
            Text       = exePresent ? "✓ V6AP engine is installed"
                                    : "Engine not installed (click Install in the Play tab)",
            FontSize   = 11, Foreground = exePresent ? success : muted,
            Margin     = new System.Windows.Thickness(0, 6, 0, 4),
        });
        panel.Children.Add(new TextBlock
        {
            Text       = dataPresent ? "✓ VVVVVV data.zip is staged — ready to play"
                                     : "✗ data.zip not provided yet (required — see below)",
            FontSize   = 11, Foreground = dataPresent ? success : warn,
            Margin     = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: VVVVVV data.zip (bring-your-own asset) ───────────────
        panel.Children.Add(new TextBlock
        {
            Text = "VVVVVV DATA.ZIP", FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });

        var dataRow = new DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dataBox = new TextBox
        {
            Text = LoadSettings().DataZipPath ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var dataBtn = new Button
        {
            Content = "Select data.zip...", Width = 130, Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dataBtn.Click += (_, _) =>
        {
            if (PromptForDataZip())
            {
                dataBox.Text = LoadSettings().DataZipPath ?? "";
                dirBox.Text  = GameDirectory; // unchanged, but refresh status text on next open
            }
        };
        DockPanel.SetDock(dataBtn, Dock.Right);
        dataRow.Children.Add(dataBtn);
        dataRow.Children.Add(dataBox);
        panel.Children.Add(dataRow);

        panel.Children.Add(new TextBlock
        {
            Text = "VVVVVV's engine is open source, but its graphics and audio are not — " +
                   "they live in data.zip and ship only with the paid game. Own VVVVVV on " +
                   "Steam or GOG, then point the launcher at its data.zip (in the VVVVVV " +
                   "install folder). The launcher copies it into its own folder and stages " +
                   "it next to VVVVVV.exe — your original file is never modified.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: How it connects ──────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "ARCHIPELAGO CONNECTION", FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "V6AP takes its connection from command-line launch options. The launcher " +
                   "passes your slot name, server (host:port) and password as -v6ap_name / " +
                   "-v6ap_ip / -v6ap_passwd each time you press Play, then runs VVVVVV.exe — " +
                   "no config file to edit and no in-game menu. The password is passed only " +
                   "as a launch argument and is never written to disk by the launcher. " +
                   "VVVVVV is a core Archipelago world, so there is no apworld to download.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("VVVVVV on Steam ↗",            SteamStoreUrl),
            ("VVVVVV on GOG ↗",              GogStoreUrl),
            ("V6AP (GitHub) ↗",             RepoUrl),
            ("VVVVVV Setup Guide ↗",        SetupGuideUrl),
            ("Archipelago Official ↗",      "https://archipelago.gg"),
        })
        {
            var btn = new Button
            {
                Content = label, HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new System.Windows.Thickness(0, 2, 0, 2), Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0), FontSize = 12, Margin = new System.Windows.Thickness(0, 0, 0, 4),
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
                    Version: el.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "",
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

    /// Resolve the latest release: version (raw tag, e.g. "AP0.5.1-3") + Windows
    /// zip asset URL. V6AP publishes normal (non-prerelease) releases, so
    /// /releases/latest is the right endpoint. Falls back to the pinned
    /// AP0.5.1-3 direct URL when the API is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? zip = PickWindowsZip(assets);
                if (zip != null)
                    return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback below */ }

        // Offline fallback: AP0.5.1-3, verified asset URL.
        return (FallbackVersion, FallbackZipUrl);
    }

    /// From a release's assets array, pick the Windows zip. The verified asset
    /// name is exactly "V6AP-win.zip" across every release; match that first, then
    /// fall back broadly to any non-source/non-Linux/non-Mac .zip.
    private static string? PickWindowsZip(JsonElement assets)
    {
        string? exact = null, winish = null, anyZip = null;

        foreach (var a in assets.EnumerateArray())
        {
            string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name == null || url == null) continue;

            string lower = name.ToLowerInvariant();
            if (!lower.EndsWith(".zip")) continue;
            if (lower.Contains("source")) continue;

            if (lower == "v6ap-win.zip")
                exact ??= url;

            if (!lower.Contains("linux") && !lower.Contains("ubuntu") &&
                !lower.Contains("mac")   && !lower.Contains("osx") && !lower.Contains("darwin"))
            {
                anyZip ??= url;
                if (lower.Contains("win") || lower.Contains("x64") || lower.Contains("x86_64"))
                    winish ??= url;
            }
        }

        return exact ?? winish ?? anyZip;
    }

    // ── Private helpers — exe resolution ──────────────────────────────────────

    /// Resolve the installed exe: prefer "VVVVVV.exe", then any "*vvvvvv*"/"*v6*"
    /// exe in the install (fuzzy), skipping helper/uninstaller exes.
    private string? ResolveGameExe()
    {
        if (File.Exists(PreferredExePath)) return PreferredExePath;
        if (!Directory.Exists(GameDirectory)) return null;
        try
        {
            foreach (string exe in Directory.EnumerateFiles(GameDirectory, "*.exe", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (name.Contains("unins") || name.Contains("setup") || name.Contains("crash"))
                    continue;
                if (name.Contains("vvvvvv") || name == "v6" || name.Contains("v6ap"))
                    return exe;
            }
        }
        catch { /* directory vanished mid-scan */ }
        return null;
    }

    // ── Private helpers — data.zip (bring-your-own asset, §11) ────────────────

    /// Open the data.zip picker, validate by CONTENT, copy into the launcher's own
    /// library (§11 — original never touched), persist the COPY's path, and stage
    /// it next to VVVVVV.exe if the engine is installed. Returns true when a valid
    /// data.zip was imported.
    private bool PromptForDataZip()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Select your VVVVVV data.zip",
            Filter = "VVVVVV asset bundle (data.zip)|data.zip;*.zip|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return false;

        string? bad = ValidateDataZip(dlg.FileName);
        if (bad != null)
        {
            MessageBox.Show(bad, "Not a valid VVVVVV data.zip",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        try
        {
            Directory.CreateDirectory(DataLibraryDirectory);
            File.Copy(dlg.FileName, DataLibraryPath, overwrite: true);

            var s = LoadSettings();
            s.DataZipPath = DataLibraryPath;
            SaveSettings(s);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not copy data.zip into the launcher library:\n{ex.Message}\n\n" +
                "Nothing was changed — your original file is untouched.",
                "data.zip import failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        EnsureDataStaged();
        return true;
    }

    /// Copy the user's library data.zip next to VVVVVV.exe so the patched engine
    /// finds it. Best effort — never throws (IsInstalled simply stays false if the
    /// asset isn't available). Only the user's supplied asset is ever staged; the
    /// engine's own files are left untouched.
    private void EnsureDataStaged()
    {
        try
        {
            if (!Directory.Exists(GameDirectory)) return;
            string? lib = LoadSettings().DataZipPath;
            if (string.IsNullOrEmpty(lib) || !File.Exists(lib)) return;

            // Skip if an identical-size data.zip is already staged.
            if (File.Exists(StagedDataPath))
            {
                try
                {
                    if (new FileInfo(StagedDataPath).Length == new FileInfo(lib).Length)
                        return;
                }
                catch { /* fall through and re-copy */ }
            }

            Directory.CreateDirectory(GameDirectory);
            File.Copy(lib, StagedDataPath, overwrite: true);
        }
        catch { /* staging is a convenience — Launch surfaces a clear error if missing */ }
    }

    /// Validate that the picked file is VVVVVV's asset bundle, by CONTENT:
    ///   * must be a readable zip archive,
    ///   * should contain VVVVVV's signature graphics entries (a "graphics/" path
    ///     or known sheets like tiles.png / sprites.png / a *.png under graphics).
    /// As a last resort a plausibly-sized zip literally named "data.zip" is
    /// accepted (store/version layouts vary; we validate it's the asset bundle
    /// without gatekeeping on a byte-exact master). Returns null when acceptable,
    /// else a short human-readable reason.
    private static string? ValidateDataZip(string path)
    {
        try
        {
            long len = new FileInfo(path).Length;
            // VVVVVV's data.zip is a few MB; reject obviously-wrong files but stay
            // generous across store/version variants.
            const long min = 256L * 1024;          // 256 KB
            const long max = 256L * 1024 * 1024;   // 256 MB
            if (len < min || len > max)
                return "That file is the wrong size for VVVVVV's data.zip. Pick the " +
                       "data.zip from your VVVVVV install folder (a few MB in size).";

            using var zip = ZipFile.OpenRead(path);

            bool looksLikeVvvvvv = false;
            foreach (var entry in zip.Entries)
            {
                string e = entry.FullName.Replace('\\', '/').ToLowerInvariant();
                if (e.StartsWith("graphics/") || e.Contains("/graphics/") ||
                    e.EndsWith("tiles.png")   || e.EndsWith("tiles2.png") ||
                    e.EndsWith("sprites.png") || e.EndsWith("flipsprites.png") ||
                    e.EndsWith("font.png")    || e.EndsWith("levelcomplete.png"))
                {
                    looksLikeVvvvvv = true;
                    break;
                }
            }

            if (looksLikeVvvvvv) return null;

            // Last resort: a valid zip literally named data.zip (covers layouts we
            // didn't anticipate) — it opened cleanly above, so it's a real archive.
            if (string.Equals(Path.GetFileName(path), "data.zip", StringComparison.OrdinalIgnoreCase))
                return null;

            return "That zip does not look like VVVVVV's data.zip (no VVVVVV graphics " +
                   "found inside). Make sure you picked data.zip from your VVVVVV install.";
        }
        catch (InvalidDataException)
        {
            return "That file is not a valid zip archive. Pick the data.zip from your " +
                   "VVVVVV install folder.";
        }
        catch
        {
            return "Could not read that file. Pick a different file and try again.";
        }
    }

    // ── Private helpers — AP launch options (verified) ────────────────────────

    /// Build the VERIFIED V6AP launch options from the session:
    ///   -v6ap_name "<slot>" -v6ap_ip <host:port> [-v6ap_passwd "<pw>"]
    /// Slot and password are quoted (the setup guide requires quoting when they
    /// contain spaces; quoting unconditionally is safe and simpler).
    private static string BuildApLaunchArgs(ApSession session)
    {
        var (host, port) = ParseServerHostPort(session.ServerUri);
        string ip = host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";

        var sb = new StringBuilder();
        sb.Append("-v6ap_name ").Append(QuoteArg(session.SlotName));
        sb.Append(" -v6ap_ip ").Append(QuoteArg(ip));
        if (!string.IsNullOrEmpty(session.Password))
            sb.Append(" -v6ap_passwd ").Append(QuoteArg(session.Password));
        return sb.ToString();
    }

    /// Wrap a value in double quotes for a Windows command line, escaping any
    /// embedded double quotes. Always quotes (harmless for values without spaces).
    private static string QuoteArg(string value)
    {
        value ??= "";
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    /// Accepts "archipelago.gg:38281", "ws://host:port", "wss://host:port", a bare
    /// hostname, and IPv6 literals (bracketed "[::1]:38281" or bare "::1").
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

    // ── Private helpers — process ─────────────────────────────────────────────

    private void StartGameProcess(string exePath, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName         = exePath,
            WorkingDirectory = GameDirectory,
            UseShellExecute  = false,
        };
        if (!string.IsNullOrEmpty(arguments))
            psi.Arguments = arguments;

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start VVVVVV (V6AP).");

        _gameProcess = proc;
        IsRunning    = true;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            IsRunning = false;
            GameExited?.Invoke(proc.ExitCode);
        };
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // Kept in this plugin's OWN JSON file so it stays a single self-contained
    // source file and does not modify Core/SettingsStore.

    private sealed class V6Settings
    {
        /// Path to the user's data.zip inside the launcher's own library copy.
        public string? DataZipPath { get; set; }
    }

    private V6Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<V6Settings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(V6Settings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal — settings just won't persist this time */ }
    }

    // ── Private helpers — download/extract ────────────────────────────────────

    private async Task DownloadAndExtractGameAsync(
        string zipUrl,
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"v6ap-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((5, $"Downloading VVVVVV (V6AP) {version}..."));
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
                        int pct = (int)(5 + 60 * downloaded / total);
                        progress.Report((pct, $"Downloading VVVVVV (V6AP)... {downloaded / 1_000_000}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((70, "Extracting..."));
            Directory.CreateDirectory(GameDirectory);
            ZipFile.ExtractToDirectory(tempZip, GameDirectory, overwriteFiles: true);

            // Release zips sometimes nest everything under a single wrapper folder;
            // flatten it so VVVVVV.exe lands directly in GameDirectory. (The V6AP
            // zip is flat — exe + dll at the root — but stay robust.)
            if (ResolveGameExe() == null)
            {
                string[] subdirs = Directory.GetDirectories(GameDirectory);
                if (subdirs.Length == 1)
                {
                    string sub = subdirs[0];
                    foreach (string fileSrc in Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories))
                    {
                        string rel     = Path.GetRelativePath(sub, fileSrc);
                        string fileDst = Path.Combine(GameDirectory, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(fileDst)!);
                        File.Move(fileSrc, fileDst, overwrite: true);
                    }
                    Directory.Delete(sub, recursive: true);
                }
            }

            progress.Report((80, "Engine files extracted."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }
}
