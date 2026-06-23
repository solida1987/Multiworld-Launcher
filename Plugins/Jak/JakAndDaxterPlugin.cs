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

namespace LauncherV2.Plugins.Jak;

// ═══════════════════════════════════════════════════════════════════════════════
// JakAndDaxterPlugin — install / launch for "Jak and Daxter: The Precursor
// Legacy" played through ArchipelaGOAL, the Archipelago integration built on
// OpenGOAL (the decompiled native-PC port of the PS2 game). This is a NATIVE
// "ConnectsItself" integration in the same family as Ship of Harkinian and
// APDOOM — the game speaks to the AP server itself, no emulator, no Lua bridge.
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// This game is STRUCTURALLY DIFFERENT from SoH/APDOOM, and the plugin is honest
// about it end-to-end. There is NO single downloadable "game zip" that the
// launcher can extract and run with an AP-connection prefill. The verified facts:
//
//   * THE GAME ENGINE is OpenGOAL: two console executables, gk.exe (the "GOAL
//     Kernel" — the running game) and goalc.exe (the "GOAL Compiler"/REPL).
//     These are installed and managed through the OPENGOAL LAUNCHER, a separate
//     GUI app from https://opengoal.dev/ (repo open-goal/launcher; Windows asset
//     is an MSI installer, e.g. OpenGOAL-Launcher_2.10.4_x64_en-US.msi — a
//     wizard installer, NOT a portable game folder).
//
//   * ARCHIPELAGOAL is a MOD installed INSIDE the OpenGOAL Launcher (Jak and
//     Daxter > Features > Mods > ArchipelaGOAL), then compiled there. It is NOT
//     a GitHub zip this launcher can download and drop in. It lands under
//     %programfiles%/OpenGOAL-Launcher/features/jak1/mods/JakMods/archipelagoal
//     (verified: worlds/jakanddaxter/__init__.py JakAndDaxterSettings default).
//
//   * THE AP CLIENT is a PYTHON client BUNDLED IN ARCHIPELAGO itself
//     (worlds/jakanddaxter/client.py, launched from the Archipelago Launcher's
//     "Jak and Daxter Client" entry). It auto-starts gk.exe + goalc.exe, drives
//     the game over the OpenGOAL REPL (port 8181) and reads game memory from
//     gk.exe (port 8112). THE AP SERVER CONNECTION IS ENTERED IN THAT
//     ARCHIPELAGO TEXT CLIENT — there is NO command-line "-apserver" arg and NO
//     config file we can pre-write. (This is unlike APDOOM's documented CLI args
//     and unlike SoH's CVar file.) So this plugin does NOT attempt a connection
//     prefill — doing so would be dishonest theatre. The post-install note and
//     settings panel say exactly this.
//
//   * GAME DATA is BRING-YOUR-OWN, exactly like SoH: OpenGOAL needs the USER'S
//     OWN legally-obtained Jak and Daxter PS2 disc, dumped to an ISO. The
//     OpenGOAL Launcher's own first-run setup wizard points at that ISO and does
//     the asset extraction/decompilation ITSELF. This plugin only copies the
//     user's ISO into its own ROM library so it is staged in one known place —
//     it does NOT and CANNOT reproduce OpenGOAL's extraction, and the note is
//     honest about that. The original ISO is never modified (§11).
//
//   * THE AP WORLD game string is "Jak and Daxter: The Precursor Legacy"
//     (verified: worlds/jakanddaxter/game_id.py jak1_name, echoed by
//     archipelago.json "game" and client.py JakAndDaxterContext.game).
//     required_client_version = (0, 5, 0); world_version 1.0.0.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. INSTALL = best-effort download + run of the OpenGOAL LAUNCHER Windows
//      installer (open-goal/launcher latest .msi, pinned 2.10.4 fallback). That
//      is the real, correct entry point to everything else. The MSI is launched
//      through its normal WIZARD (we do NOT silent-install someone else's app);
//      the user completes it. We then stamp our own version file so the launcher
//      tile reflects "installed".
//   2. ISO = bring-your-own picker; validated loosely by size (a Jak PS2 ISO is
//      ~1.5–4.7 GB); copied into Games/ROMs/jakanddaxter/ (original untouched).
//   3. The remaining steps are GUI-only inside the OpenGOAL Launcher and CANNOT
//      be scripted from here (install vanilla Jak from the ISO → install the
//      ArchipelaGOAL mod → Compile). The plugin presents these as clear,
//      SoH-style guided instructions in the post-install note and settings panel,
//      with direct links to opengoal.dev, the ArchipelaGOAL repo, the official
//      setup guide, and archipelago.gg.
//   4. LAUNCH = open the installed OpenGOAL Launcher (the real entry point).
//      ConnectsItself = true (the ArchipelaGOAL Python client owns the slot — the
//      launcher must NOT hold its own ApClient on the same slot). The actual AP
//      connection is made in the Archipelago "Jak and Daxter Client" by the user,
//      which the note states plainly. SupportsStandalone = true (the OpenGOAL
//      Launcher plays plain, un-randomized Jak perfectly well).
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", SoH-style) ────────────────
//   * The OpenGOAL Launcher installs as a normal Windows program; its installed
//     exe is typically "OpenGOAL-Launcher.exe" under
//     %LOCALAPPDATA%/Programs/OpenGOAL-Launcher or %programfiles%. We do NOT try
//     to drive that install path — IsInstalled is tracked by OUR OWN version
//     stamp written after the MSI wizard is launched, plus a best-effort scan of
//     the common install locations so a pre-existing OpenGOAL install is honored.
//     If neither is found, the tile simply reads "not installed".
//   * Launch resolves the OpenGOAL Launcher exe from the common install
//     locations; if it cannot be found, LaunchAsync surfaces a clear message
//     pointing the user at opengoal.dev rather than failing opaquely.
//   * One launcher-side setting (the ISO path) is stored in this plugin's OWN
//     JSON sidecar (Games/ROMs/jakanddaxter/jak_launcher.json) so the plugin
//     stays a single self-contained file and does NOT modify Core/SettingsStore
//     (same approach as Doom1993Plugin).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class JakAndDaxterPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    // The OpenGOAL Launcher is the real distribution entry point. We install it
    // (the user then installs vanilla Jak + the ArchipelaGOAL mod inside it).
    private const string OG_OWNER = "open-goal";
    private const string OG_REPO  = "launcher";
    private const string OpenGoalRepoUrl   = $"https://github.com/{OG_OWNER}/{OG_REPO}";
    private const string OpenGoalSite      = "https://opengoal.dev/";
    private const string OpenGoalInstallDocs = "https://opengoal.dev/docs/usage/installation";
    private const string GH_OG_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{OG_OWNER}/{OG_REPO}/releases/latest";

    // ArchipelaGOAL — the AP integration (a mod installed INSIDE the OpenGOAL
    // Launcher, and the AP fork). Used for links + news, NOT for a zip install.
    private const string ArchipelaGoalRepoUrl = "https://github.com/ArchipelaGOAL/ArchipelaGOAL";
    private const string ArchipelaGoalApForkReleasesUrl =
        "https://api.github.com/repos/ArchipelaGOAL/Archipelago/releases";
    private const string JakModsSite = "https://jakmods.dev/";
    private const string SetupGuideUrl =
        "https://archipelago.gg/tutorial/Jak%20and%20Daxter%20The%20Precursor%20Legacy/setup/en";

    /// Pinned fallback for the OpenGOAL Launcher when the GitHub API is
    /// unreachable. v2.10.4 verified live 2026-06-14; the Windows installer asset
    /// is the x64 en-US MSI. The API path is the normal route; this is the net.
    private const string FallbackVersion = "2.10.4";
    private const string FallbackMsiName = "OpenGOAL-Launcher_2.10.4_x64_en-US.msi";
    private static readonly string FallbackMsiUrl =
        $"{OpenGoalRepoUrl}/releases/download/v{FallbackVersion}/{FallbackMsiName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    /// Installed-version stamp, written after the OpenGOAL Launcher installer is
    /// launched. (We track our own intent; the MSI installs OpenGOAL elsewhere.)
    private const string VersionFileName = "jak_opengoal_version.dat";

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "jakanddaxter";
    public string DisplayName => "Jak and Daxter: The Precursor Legacy";
    public string Subtitle    => "Native PC · built-in Archipelago";

    /// EXACT AP game string — verified against worlds/jakanddaxter/game_id.py
    /// (jak1_name) and archipelago.json.
    public string ApWorldName => "Jak and Daxter: The Precursor Legacy";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "jakanddaxter.png");

    public string ThemeAccentColor => "#2E7D32";   // Precursor / Green Eco
    public string[] GameBadges     => new[] { "Requires Jak & Daxter ISO" };

    public string Description =>
        "Jak and Daxter: The Precursor Legacy, the 2001 Naughty Dog platformer, " +
        "played through ArchipelaGOAL — an Archipelago integration built on " +
        "OpenGOAL, the native-PC decompilation of the original PS2 game. Power " +
        "Cells, Scout Flies, moves, Precursor Orbs and more are shuffled into the " +
        "multiworld, and the game connects to the Archipelago server itself — no " +
        "emulator, no Lua bridge. OpenGOAL ships no game data: you must supply " +
        "your own legally-obtained Jak and Daxter disc (dumped to an ISO), and " +
        "OpenGOAL extracts the game assets from it the first time you set it up. " +
        "The launcher installs the OpenGOAL Launcher for you and guides the rest.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means: we stamped our version file after launching the OpenGOAL
    /// Launcher installer, OR an OpenGOAL Launcher exe already exists on this PC.
    public bool IsInstalled => File.Exists(VersionFilePath) || ResolveOpenGoalLauncherExe() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where the OpenGOAL Launcher INSTALLER (.msi) is downloaded to and where we
    /// keep our own version stamp. The OpenGOAL Launcher itself installs into
    /// Windows' standard program locations (we do not relocate it).
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "JakAndDaxter");

    private string VersionFilePath => Path.Combine(GameDirectory, VersionFileName);

    /// The launcher's own ROM-library copy of the user's Jak ISO (§11).
    private string RomLibraryDirectory
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained file — same as Doom1993Plugin).
    private string SettingsSidecarPath
        => Path.Combine(RomLibraryDirectory, "jak_launcher.json");

    /// Jak PS2 ISO accepted by OpenGOAL's setup. Both NTSC and PAL dumps work
    /// (per the setup guide). Validated loosely by extension + size; OpenGOAL is
    /// the authoritative validator at extraction time.
    private static readonly string[] IsoExtensions = { ".iso", ".bin" };

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // ArchipelaGOAL's Python AP client reports checks/items/goal to the AP server
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
                : (ResolveOpenGoalLauncherExe() != null ? "installed" : null);
        }
        catch
        {
            InstalledVersion = null;
        }
            try
        {
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(OG_OWNER, OG_REPO, ct));
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
        // 1. Resolve the latest OpenGOAL Launcher installer (pinned fallback).
        progress.Report((2, "Checking the latest OpenGOAL Launcher release..."));
        var (version, msiUrl) = await ResolveLatestOpenGoalAsync(ct);
        AvailableVersion = version;

        if (msiUrl == null)
            throw new InvalidOperationException(
                "Could not find the OpenGOAL Launcher Windows installer on GitHub. " +
                "Check your internet connection, or download it manually from " +
                OpenGoalSite + " (see " + OpenGoalRepoUrl + "/releases).");

        // 2. Download the installer.
        string msiPath = await DownloadInstallerAsync(msiUrl, version, progress, ct);

        // 3. Launch the installer's normal WIZARD. We do NOT silent-install
        //    another project's app — the user completes the wizard. This call
        //    returns once the installer has been started; the user finishes it.
        progress.Report((90, "Starting the OpenGOAL Launcher installer..."));
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = msiPath,
                UseShellExecute = true,   // let Windows Installer (msiexec) handle the .msi
            });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Downloaded the OpenGOAL Launcher installer but could not start it " +
                $"automatically ({ex.Message}). You can run it yourself:\n{msiPath}");
        }

        // 4. Stamp our own version file so the tile reflects the install intent.
        Directory.CreateDirectory(GameDirectory);
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        progress.Report((100,
            $"OpenGOAL Launcher {version} installer started. Finish the wizard, then " +
            "in the OpenGOAL Launcher: install Jak and Daxter from your ISO, open " +
            "Features > Mods > ArchipelaGOAL, install it, then Advanced > Compile. " +
            "See Settings for the full guided steps. Pick your Jak ISO in Settings " +
            "if you have not already."));
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
        // HONEST: the AP server connection for this game is made INSIDE the
        // Archipelago "Jak and Daxter Client" (the bundled Python client), which
        // auto-launches gk.exe + goalc.exe and connects over the OpenGOAL REPL.
        // There is no command-line / config prefill we can apply here (verified —
        // see header). So launching "the game" from this tile means opening the
        // OpenGOAL Launcher (the real entry point); the user then runs the
        // Archipelago client and connects with the session credentials.
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the ArchipelaGOAL client is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartOpenGoalLauncher();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) Jak via the OpenGOAL Launcher is fully supported.
    public bool SupportsStandalone => true;

    /// ArchipelaGOAL's Python AP client owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartOpenGoalLauncher();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // We only ever started the OpenGOAL Launcher GUI from here; the game/
        // compiler/AP-client are separate processes owned by OpenGOAL + the
        // Archipelago client. Kill what we launched; never touch the AP client.
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // No plaintext AP password is ever written to disk by this plugin (the
        // connection is entered in the Archipelago client), so there is nothing
        // to scrub — but clear our handle defensively.
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // ArchipelaGOAL's bundled client receives items from the AP server
        // directly; there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // ArchipelaGOAL renders its own AP status in its client + in-game HUD.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Unverified-offline honesty header ─────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "Jak and Daxter runs on OpenGOAL with the ArchipelaGOAL mod. The " +
                   "launcher installs the OpenGOAL Launcher and stages your ISO; the " +
                   "vanilla-Jak install, the ArchipelaGOAL mod install, the compile " +
                   "step, and the Archipelago connection are all done inside the " +
                   "OpenGOAL Launcher and the Archipelago \"Jak and Daxter Client\" " +
                   "(see the guided steps below). These external steps are not " +
                   "verified by this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: Installer / version ──────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "OPENGOAL LAUNCHER", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? ogExe = ResolveOpenGoalLauncherExe();
        panel.Children.Add(new TextBlock
        {
            Text       = ogExe != null
                            ? "✓ OpenGOAL Launcher found: " + ogExe
                            : (File.Exists(VersionFilePath)
                                ? "Installer was started — finish the OpenGOAL Launcher wizard if you have not."
                                : "Not installed (click Install in the Play tab to download the OpenGOAL Launcher)."),
            FontSize   = 11, Foreground = ogExe != null ? success : muted,
            TextWrapping = TextWrapping.Wrap,
            Margin     = new Thickness(0, 0, 0, 6),
        });

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text = GameDirectory, IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Where the launcher downloads the OpenGOAL Launcher installer and " +
                          "keeps its own version stamp. OpenGOAL itself installs into Windows' " +
                          "standard program location.",
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
                Title            = "Select where to download the OpenGOAL Launcher installer",
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

        // ── Section: Jak ISO ──────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "JAK AND DAXTER ISO", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 12, 0, 8),
        });

        var isoRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var isoBox = new TextBox
        {
            Text = LoadIsoPath() ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var isoBtn = new Button
        {
            Content = "Select ISO...", Width = 110, Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        isoBtn.Click += (_, _) =>
        {
            if (PromptForIsoFile())
                isoBox.Text = LoadIsoPath() ?? "";
        };
        DockPanel.SetDock(isoBtn, Dock.Right);
        isoRow.Children.Add(isoBtn);
        isoRow.Children.Add(isoBox);
        panel.Children.Add(isoRow);

        panel.Children.Add(new TextBlock
        {
            Text = "OpenGOAL needs your own legally-obtained Jak and Daxter disc, dumped " +
                   "to an ISO (NTSC or PAL both work). The launcher copies it into its own " +
                   "folder — your original file is never modified. OpenGOAL does its own " +
                   "asset extraction from the ISO during its first-run setup wizard; this " +
                   "launcher does not (and cannot) extract the assets itself. You point " +
                   "OpenGOAL at the ISO when it asks.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "GUIDED SETUP (done inside the OpenGOAL Launcher)", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 8, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Install the OpenGOAL Launcher (use Install on the Play tab, or get it from opengoal.dev).",
            "2. In the OpenGOAL Launcher, install a vanilla copy of Jak and Daxter from your ISO.",
            "3. Click the Jak and Daxter logo > Features > Mods > ArchipelaGOAL, and install it.",
            "4. Click Advanced > Compile, and wait for it to finish.",
            "5. To play: open the Archipelago Launcher and run the \"Jak and Daxter Client\". " +
                "It starts the game + compiler for you. When the title screen says " +
                "\"CONNECT TO ARCHIPELAGO NOW\", connect to your server in that client.",
            "Note: do NOT press Play on the mod page in the OpenGOAL Launcher for AP games — " +
                "the Archipelago client must launch the game so they can talk to each other.",
        })
        {
            panel.Children.Add(new TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("OpenGOAL (opengoal.dev) ↗",        OpenGoalSite),
            ("OpenGOAL install docs ↗",          OpenGoalInstallDocs),
            ("ArchipelaGOAL (GitHub) ↗",         ArchipelaGoalRepoUrl),
            ("Jak and Daxter Setup Guide ↗",     SetupGuideUrl),
            ("Archipelago Official ↗",           "https://archipelago.gg"),
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
        // Prefer the ArchipelaGOAL AP-fork releases (game-integration news); fall
        // back to nothing on any failure. (The OpenGOAL Launcher has its own
        // cadence, but the AP-relevant news is the ArchipelaGOAL fork.)
        try
        {
            string json = await _http.GetStringAsync(ArchipelaGoalApForkReleasesUrl, ct);
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

    /// "v2.10.4" → "2.10.4" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest OpenGOAL Launcher release: version + Windows installer
    /// (.msi) URL. Prefers the x64 en-US MSI; falls back to any x64/win .msi.
    /// Falls back to the pinned 2.10.4 direct URL when the API is unreachable.
    private async Task<(string Version, string? MsiUrl)> ResolveLatestOpenGoalAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_OG_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? preferred = null;   // x64 en-US .msi
                string? anyMsi    = null;   // any windows-ish .msi
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    // Only the bare .msi installer — never the .msi.zip / .msi.sig
                    // sidecar assets that share the prefix.
                    if (!lower.EndsWith(".msi")) continue;

                    anyMsi ??= url;
                    if (preferred == null &&
                        (lower.Contains("x64") || lower.Contains("amd64") || lower.Contains("win")))
                        preferred = url;
                }
                string? msi = preferred ?? anyMsi;
                if (msi != null)
                    return (version, msi);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (FallbackVersion, FallbackMsiUrl);
    }

    // ── Private helpers — OpenGOAL Launcher exe resolution ────────────────────

    /// Best-effort scan of the common Windows install locations for an existing
    /// OpenGOAL Launcher install, so a pre-existing install is honored. Returns
    /// the exe path or null. We do NOT depend on this for install (the MSI handles
    /// that); it only improves the "is it here?" signal and enables Launch.
    private static string? ResolveOpenGoalLauncherExe()
    {
        foreach (string root in CandidateOpenGoalRoots())
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                // Direct hit at the root.
                string direct = Path.Combine(root, "OpenGOAL-Launcher.exe");
                if (File.Exists(direct)) return direct;

                // Otherwise scan a shallow tree for the launcher exe.
                foreach (string exe in Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                    if (name.Contains("opengoal") && name.Contains("launcher"))
                        return exe;
                }
            }
            catch { /* permission / vanished — try the next candidate */ }
        }
        return null;
    }

    /// Candidate parent folders an OpenGOAL Launcher install may live under
    /// (Tauri apps typically land under %LOCALAPPDATA%\Programs; the wizard can
    /// also target Program Files).
    private static IEnumerable<string> CandidateOpenGoalRoots()
    {
        string? localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string? prog     = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string? progX86  = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        if (!string.IsNullOrEmpty(localApp))
        {
            yield return Path.Combine(localApp, "Programs", "OpenGOAL-Launcher");
            yield return Path.Combine(localApp, "OpenGOAL-Launcher");
        }
        if (!string.IsNullOrEmpty(prog))
            yield return Path.Combine(prog, "OpenGOAL-Launcher");
        if (!string.IsNullOrEmpty(progX86))
            yield return Path.Combine(progX86, "OpenGOAL-Launcher");
    }

    /// Open the installed OpenGOAL Launcher. If we cannot find it, surface a
    /// clear, honest message (rather than silently doing nothing) pointing at the
    /// install step. Never throws into the caller's launch task in a way that
    /// looks like a crash — we raise a descriptive FileNotFoundException the UI
    /// can show.
    private void StartOpenGoalLauncher()
    {
        string? exe = ResolveOpenGoalLauncherExe();
        if (exe == null)
            throw new FileNotFoundException(
                "The OpenGOAL Launcher was not found. Click Install Game to download " +
                "and run its installer, finish the wizard, then try again. (You can " +
                "also install it from https://opengoal.dev/.)",
                "OpenGOAL-Launcher.exe");

        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = exe,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? GameDirectory,
            UseShellExecute  = true,
        }) ?? throw new InvalidOperationException("Failed to start the OpenGOAL Launcher.");

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
        catch { /* some shell-launched processes don't expose Exited — non-fatal */ }
    }

    // ── Private helpers — ISO (bring-your-own) ────────────────────────────────

    /// Open the ISO picker, validate by size (a Jak PS2 ISO is ~1.5–4.7 GB),
    /// copy into the launcher's own ROM library (§11 — original never touched),
    /// and persist the COPY's path. Returns true when an ISO was imported.
    private bool PromptForIsoFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Select your Jak and Daxter ISO",
            Filter = "Disc image (*.iso;*.bin)|*.iso;*.bin|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return false;

        string? bad = ValidateJakIso(dlg.FileName);
        if (bad != null)
        {
            MessageBox.Show(bad, "Not a valid Jak and Daxter ISO",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        try
        {
            Directory.CreateDirectory(RomLibraryDirectory);
            string dst = Path.Combine(RomLibraryDirectory, Path.GetFileName(dlg.FileName));
            File.Copy(dlg.FileName, dst, overwrite: true);
            SaveIsoPath(dst);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not copy the ISO into the launcher library:\n{ex.Message}\n\n" +
                "Nothing was changed — your original file is untouched.",
                "ISO import failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    /// Loose content check for a Jak PS2 ISO: known extension + plausible size.
    /// A single-layer PS2 DVD is up to ~4.7 GB; Jak fits comfortably above
    /// ~1.5 GB. We deliberately do NOT gatekeep on a specific MD5 (NTSC and PAL
    /// masters differ, and OpenGOAL is the authoritative validator at extraction).
    /// Returns null when acceptable, else a short human-readable reason.
    private static string? ValidateJakIso(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (Array.IndexOf(IsoExtensions, ext) < 0)
            return "That file is not a disc image (expected a .iso). Dump your Jak and " +
                   "Daxter disc to an ISO and pick that file.";

        try
        {
            long len = new FileInfo(path).Length;
            const long min = 1_400L * 1024 * 1024;   // ~1.4 GB floor
            const long max = 4_900L * 1024 * 1024;   // ~4.9 GB ceiling (single-layer DVD + slack)
            if (len < min)
                return "That file is too small to be a Jak and Daxter PS2 disc image. " +
                       "Make sure it is a full ISO dump of the disc (around 1.5–4 GB).";
            if (len > max)
                return "That file is larger than a single-layer PS2 disc image. Make sure " +
                       "you picked your Jak and Daxter ISO, not something else.";
        }
        catch
        {
            return "Could not read that file. Pick a different ISO and try again.";
        }
        return null;
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // One launcher-side setting (the ISO path) is kept in this plugin's OWN JSON
    // file so it stays a single self-contained source file and does not modify
    // Core/SettingsStore. BOM-less UTF-8, read-modify-write (same as Doom).

    private sealed class JakSettings
    {
        public string? IsoPath { get; set; }
    }

    private JakSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<JakSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(JakSettings s)
    {
        try
        {
            Directory.CreateDirectory(RomLibraryDirectory);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal — setting just won't persist this time */ }
    }

    private string? LoadIsoPath()          => LoadSettings().IsoPath;
    private void    SaveIsoPath(string p)  { var s = LoadSettings(); s.IsoPath = p; SaveSettings(s); }

    // ── Private helpers — download installer ──────────────────────────────────

    /// Download the OpenGOAL Launcher installer (.msi) to GameDirectory and
    /// return its local path. (We keep the installer rather than a temp file so a
    /// failed/cancelled wizard can be re-run by the user.)
    private async Task<string> DownloadInstallerAsync(
        string msiUrl,
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(GameDirectory);
        string fileName = SafeInstallerName(msiUrl, version);
        string dst      = Path.Combine(GameDirectory, fileName);

        progress.Report((5, $"Downloading OpenGOAL Launcher {version} installer..."));
        using var response = await _http.GetAsync(
            msiUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long total      = response.Content.Headers.ContentLength ?? -1;
        long downloaded = 0;

        await using (var src = await response.Content.ReadAsStreamAsync(ct))
        await using (var outFs = File.Create(dst))
        {
            var buf = new byte[81920];
            int bytesRead;
            while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
            {
                await outFs.WriteAsync(buf.AsMemory(0, bytesRead), ct);
                downloaded += bytesRead;
                if (total > 0)
                {
                    int pct = (int)(5 + 80 * downloaded / total);
                    progress.Report((pct, $"Downloading OpenGOAL Launcher... {downloaded / 1_000_000}MB"));
                }
            }
            await outFs.FlushAsync(ct);
        }

        return dst;
    }

    /// Derive a safe installer filename from the download URL, falling back to a
    /// versioned default. Guards against path traversal in the URL's last segment.
    private static string SafeInstallerName(string url, string version)
    {
        try
        {
            string last = url;
            int slash = last.LastIndexOf('/');
            if (slash >= 0 && slash + 1 < last.Length) last = last[(slash + 1)..];
            int q = last.IndexOf('?');
            if (q >= 0) last = last[..q];
            last = Path.GetFileName(last); // strip any stray separators
            if (!string.IsNullOrWhiteSpace(last) && last.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                return last;
        }
        catch { /* fall through to default */ }
        return $"OpenGOAL-Launcher_{version}_x64_en-US.msi";
    }
}
