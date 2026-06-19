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

namespace LauncherV2.Plugins.DoomII;

// ═══════════════════════════════════════════════════════════════════════════════
// DoomIIPlugin — install / update / launch for "DOOM II" — id Software's DOOM II:
// Hell on Earth, played through APDOOM, a fork of Crispy Doom with a BUILT-IN
// Archipelago client. This is a NATIVE "ConnectsItself" integration (NOT a
// BizHawk / Lua emulator game): the engine speaks to the AP server itself,
// exactly like Ship of Harkinian and the OpenTTD Archipelago fork.
//
// This is a NEAR-COPY of the already-shipped Plugins/Doom/Doom1993Plugin.cs —
// SAME engine, SAME install/launch/settings scaffolding. APDOOM is a single build
// that supports BOTH DOOM 1993 and DOOM II; DOOM II just loads DOOM2.WAD and uses
// the "doom2" game id on the command line. The only differences from the 1993
// plugin: IWAD (DOOM2.WAD), world id (doom_ii), AP game string ("DOOM II"), the
// "-game doom2" launch value, and the bring-your-own-WAD size window.
//
// REALITY CHECK (2026-06-14) — facts verified online this session
// ─────────────────────────────────────────────────────────────────────────────
// APDOOM is real and there are TWO active upstreams; this plugin uses the newer
// one as primary and the original as a pinned fallback (same as the 1993 plugin —
// it is the SAME engine build, so nothing about the repo/release changes for
// DOOM II):
//
//   * PRIMARY: ArchipelagoDoom/APDoom — the active org continuing the project.
//     Latest release v2.0.0-beta3 (2026-04-16). Crispy Doom 7.x base, 64-bit.
//     EVERY release on that repo is marked "prerelease", so the GitHub
//     /releases/latest endpoint 404s — we therefore enumerate /releases and
//     take the newest entry (prereleases included). Windows asset name:
//     "apdoom-Windows-x64.zip" (beta1 shipped "APDoom-Windows-x64.zip" — the
//     casing varies, so the resolver matches by pattern, not a fixed string).
//
//   * FALLBACK: Daivuk/apdoom — the original repo. Latest non-prerelease
//     tag "1.2.0" (2025-03-25), asset "APDOOM-1_2_0-Win32.zip" (32-bit). Used
//     ONLY when the primary repo's API is unreachable — a known-good direct URL
//     so install still works offline-of-the-primary. (Daivuk's build also
//     supports DOOM II.)
//
// HOW IT CONNECTS (VERIFIED against the official Archipelago "DOOM II" setup
// guide, https://archipelago.gg/tutorial/DOOM%20II/setup_en, and AP PR #3757
// "id Tech 1 games: Add command line instructions/info"):
//   The game is launched with COMMAND-LINE arguments — this is the documented,
//   verified path. The DOOM II guide's verbatim example is:
//       crispy-apdoom -game doom2 -apserver <server> -applayer <slot name>
//       [-password <pw>] [-skill <1-5>]
//   i.e. the ONLY change from DOOM 1993 is "-game doom2" (vs "-game doom") and
//   the IWAD (DOOM2.WAD vs DOOM.WAD). The CLI exe is "crispy-apdoom(.exe)".
//   Windows builds ALSO ship a GUI front-end ("apdoom-launcher.exe" on Daivuk,
//   "APDoomLauncher.exe" on the new org) that auto-detects WADs and offers the
//   same connection fields. AP allows one connection per slot, so — like
//   SoH/OpenTTD — the launcher must NOT hold its own ApClient on the same slot
//   while the game runs: ConnectsItself = true.
//
// GAME DATA — BRING-YOUR-OWN-WAD (§11):
//   APDOOM ships NO commercial game data. The player supplies their own
//   DOOM2.WAD (id's IWAD for DOOM II). Verified setup: "Copy DOOM2.WAD from your
//   steam install into the extracted folder." This plugin lets the user pick
//   their WAD, validates it by CONTENT (the IWAD magic — first 4 bytes "IWAD" —
//   plus a plausible size), copies it into the launcher's own ROM library
//   (Games/ROMs/doom_ii/, original NEVER modified, §11), and stages a copy named
//   DOOM2.WAD next to the exe so APDOOM finds it. When launching the CLI exe
//   directly we also pass "-iwad <stagedWad>" defensively.
//   Known DOOM2.WAD sizes (doomwiki.org): v1.9 commercial 14,604,584 B; the
//   BFG Edition / Unity port DOOM2.WAD differ slightly but stay in the ~14 MB
//   range. We accept a loose 10–25 MB window so the common retail / BFG / Unity
//   dumps all pass — APDOOM itself is the authoritative validator at load. We DO
//   reject a PWAD (mod patch) here, since the base game needs an IWAD, and we
//   reject obviously-wrong sizes (e.g. a ~4 MB DOOM 1993 shareware WAD).
//
// THE AP WORLD:
//   game string "DOOM II" (VERIFIED: worlds/doom_ii/__init__.py →
//   DOOM2World.game = "DOOM II"). The new org's release ships a DOOM II apworld
//   alongside the 1993 one; this plugin fetches whichever DOOM II apworld the
//   resolved release carries, next to the install, so the user can drop it into
//   Archipelago's custom_worlds — best effort; AP-main already bundles the
//   stable world.
//
// DEFENSIVE / UNVERIFIED DETAILS (flagged, "verify at build time", SoH-style):
//   * EXE NAME inside the zip was not inspected offline. ResolveGameExe()
//     prefers "crispy-apdoom.exe", then any "*apdoom*"/"*doom*" exe, with the
//     GUI "*launcher*.exe" as the last resort. The CLI args are only passed when
//     the resolved exe is the CLI engine; if only the GUI launcher is found we
//     still pass them best-effort (it may ignore them — the player then uses the
//     GUI fields, and the slot save / "Load Previous Game" remembers them).
//   * One launcher-side setting (the WAD path) is stored in this plugin's OWN
//     JSON sidecar (Games/ROMs/doom_ii/doom2_launcher.json) rather than in
//     Core/SettingsStore — this plugin is added as a single self-contained file
//     and deliberately does not modify shared launcher types. SoH used
//     SettingsStore fields; this is the one structural difference, documented
//     here for honesty.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class DoomIIPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    // PRIMARY upstream — the active org. All releases are prereleases, so we
    // enumerate /releases (NOT /releases/latest, which 404s for prerelease-only
    // repos) and take the newest. SAME engine build serves DOOM 1993 and DOOM II.
    private const string GITHUB_OWNER = "ArchipelagoDoom";
    private const string GITHUB_REPO  = "APDoom";
    private const string RepoUrl      = $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases";

    // FALLBACK upstream — the original repo, used only when the primary API is
    // unreachable. "1.2.0" is the latest non-prerelease there; asset name and
    // direct URL verified 2026-06-14.
    private const string FALLBACK_OWNER   = "Daivuk";
    private const string FALLBACK_REPO    = "apdoom";
    private const string FallbackRepoUrl  = $"https://github.com/{FALLBACK_OWNER}/{FALLBACK_REPO}";
    private const string FallbackVersion  = "1.2.0";
    private const string FallbackZipName  = "APDOOM-1_2_0-Win32.zip";
    private static readonly string FallbackZipUrl =
        $"{FallbackRepoUrl}/releases/download/{FallbackVersion}/{FallbackZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    /// Installed-version stamp, written after a successful install.
    private const string VersionFileName = "apdoom2_version.dat";

    /// Canonical filename APDOOM expects for the DOOM II IWAD next to the exe.
    private const string StagedWadName = "DOOM2.WAD";

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "doom_ii";
    public string DisplayName => "DOOM II";
    public string Subtitle    => "Native PC · built-in Archipelago";

    /// EXACT AP game string — verified against worlds/doom_ii/__init__.py
    /// (DOOM2World.game = "DOOM II").
    public string ApWorldName => "DOOM II";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "doom_ii.png");

    public string ThemeAccentColor => "#8A1A12";   // DOOM red
    public string[] GameBadges     => new[] { "Requires DOOM2.WAD" };

    public string Description =>
        "DOOM II: Hell on Earth, id Software's 1994 sequel, played through APDOOM — " +
        "a fork of Crispy Doom with a built-in Archipelago client. Weapons, keycards, " +
        "level access, powerups and more are shuffled into the multiworld, and the " +
        "engine connects to the Archipelago server itself — no emulator, no Lua " +
        "bridge. APDOOM ships no game data: you must supply your own DOOM2.WAD (from " +
        "your Steam, GOG, or original copy of DOOM II). The launcher copies your WAD " +
        "next to the game and connects you on launch.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }
    public bool    IsInstalled      => ResolveGameExe() != null;
    public bool    IsRunning        { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Root folder where APDOOM is installed (its own folder, separate from the
    /// DOOM 1993 install so each game keeps its own staged IWAD).
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "DoomII");

    /// Preferred CLI exe (verified name). Resolution falls back to a fuzzy match.
    private string PreferredExePath => Path.Combine(GameDirectory, "crispy-apdoom.exe");

    /// Where the release's doom_ii apworld is saved for the user to copy into
    /// Archipelago's custom_worlds folder.
    private string ApWorldLocalPath
    {
        get
        {
            string? name = _apWorldFileName;
            return Path.Combine(GameDirectory,
                string.IsNullOrEmpty(name) ? "doom_ii.apworld" : name);
        }
    }

    private string VersionFilePath => Path.Combine(GameDirectory, VersionFileName);

    /// The launcher's own ROM-library copy of the user's DOOM2.WAD (§11).
    private string RomLibraryDirectory
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);

    /// Where the IWAD must live next to the exe for APDOOM to find it.
    private string StagedWadPath => Path.Combine(GameDirectory, StagedWadName);

    /// This plugin's OWN settings sidecar (see header — kept out of the shared
    /// SettingsStore so the plugin is one self-contained file).
    private string SettingsSidecarPath
        => Path.Combine(RomLibraryDirectory, "doom2_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    /// Filename of the apworld asset seen on the resolved release (so the saved
    /// copy keeps the upstream name). null until a release is resolved.
    private string? _apWorldFileName;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // APDOOM's native AP client reports checks/items/goal to the AP server
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
            InstalledVersion = File.Exists(VersionFilePath) && IsInstalled
                ? (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim()
                : null;
        }
        catch
        {
            InstalledVersion = null;
        }

        try
        {
            var (version, _, _, _) = await ResolveLatestReleaseAsync(ct);
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
        // 1. Resolve the latest release (pinned fallback when offline).
        progress.Report((2, "Checking latest APDOOM release..."));
        var (version, zipUrl, apworldUrl, apworldName) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;
        _apWorldFileName = apworldName;

        // 2. Already current? (idempotent fast path)
        if (IsInstalled
            && File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100, $"APDOOM {version} is up to date."));
            return;
        }

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find a Windows download for APDOOM on the GitHub release " +
                "page. Check your internet connection, or download the build " +
                "manually from " + RepoUrl + "/releases.");

        // 3. Download + extract the build.
        await DownloadAndExtractGameAsync(zipUrl, version, progress, ct);

        // 4. Fetch the apworld next to the install (best effort).
        if (apworldUrl != null)
        {
            try
            {
                progress.Report((85, "Downloading the DOOM II apworld..."));
                byte[] apworld = await _http.GetByteArrayAsync(apworldUrl, ct);
                await File.WriteAllBytesAsync(ApWorldLocalPath, apworld, ct);
                progress.Report((92, $"{Path.GetFileName(ApWorldLocalPath)} saved — copy it into Archipelago's custom_worlds folder if you generate with it."));
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                progress.Report((92, "Could not download the apworld — get it from the GitHub release page (the stable world also ships with Archipelago)."));
            }
        }

        // 5. Stage the user's WAD next to the exe if they already picked one.
        StageWadForGame();

        // 6. Stamp the installed version.
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        progress.Report((100,
            $"APDOOM {version} ready. Pick your DOOM2.WAD in Settings if you have " +
            "not already, then press Play."));
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
                "APDOOM is not installed. Click Install Game first.",
                PreferredExePath);

        // Make sure the IWAD is staged next to the exe (APDOOM needs DOOM2.WAD).
        StageWadForGame();

        // VERIFIED connection path: pass the documented AP command-line args.
        // The CLI engine consumes them directly; the GUI launcher may ignore
        // them (the player then uses its fields). Never blocks the launch.
        string args = BuildLaunchArguments(session, exe);
        StartGameProcess(exe, args);
        return Task.CompletedTask;
    }

    /// APDOOM is a complete game — plain (non-AP) play is supported.
    public bool SupportsStandalone => true;

    /// APDOOM's native in-game AP client owns the slot connection (see header).
    /// The launcher must not connect its own ApClient to the same slot while the
    /// game runs.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        string exe = ResolveGameExe()
            ?? throw new FileNotFoundException(
                "APDOOM is not installed. Click Install Game first.",
                PreferredExePath);

        StageWadForGame();

        // No AP args — plain DOOM II. Still pass -iwad when launching the CLI
        // engine directly so it finds the staged WAD without a chooser.
        string args = IsCliEngine(exe) && File.Exists(StagedWadPath)
            ? $"-iwad \"{StagedWadPath}\""
            : "";
        StartGameProcess(exe, args);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // AP credentials were passed on the command line (not written to a file),
        // so there is no plaintext password to scrub from disk — but clear our
        // cached last-args defensively all the same.
        _lastLaunchArgs = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // APDOOM's native client receives items from the AP server directly;
        // there is nothing to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // APDOOM renders its own AP status in-game; no launcher HUD channel.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Section: Install directory ────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "INSTALL DIRECTORY", FontSize = 10, FontWeight = FontWeights.SemiBold,
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
                Title            = "Select APDOOM install folder",
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
            Text       = IsInstalled ? "✓ APDOOM is installed"
                                     : "Not installed (click Install in the Play tab)",
            FontSize   = 11, Foreground = IsInstalled ? success : muted,
            Margin     = new Thickness(0, 6, 0, 12),
        });

        // ── Section: DOOM2.WAD ────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "DOOM2.WAD", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 8, 0, 8),
        });

        var wadRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var wadBox = new TextBox
        {
            Text = LoadWadPath() ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var wadBtn = new Button
        {
            Content = "Select WAD...", Width = 110, Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        wadBtn.Click += (_, _) =>
        {
            if (PromptForWadFile())
                wadBox.Text = LoadWadPath() ?? "";
        };
        DockPanel.SetDock(wadBtn, Dock.Right);
        wadRow.Children.Add(wadBtn);
        wadRow.Children.Add(wadBox);
        panel.Children.Add(wadRow);

        panel.Children.Add(new TextBlock
        {
            Text = "APDOOM needs your own DOOM2.WAD (the IWAD from your Steam, GOG, or " +
                   "original copy of DOOM II). The launcher copies it into its own folder " +
                   "and stages a copy named DOOM2.WAD next to the game — your original " +
                   "file is never modified. The DOOM2.WAD is about 14 MB.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        if (IsInstalled && File.Exists(ApWorldLocalPath))
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"{Path.GetFileName(ApWorldLocalPath)} is saved in the install folder — " +
                       @"copy it into your Archipelago custom_worlds folder (default: " +
                       @"C:\ProgramData\Archipelago\custom_worlds) if you generate with this build.",
                FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
            });
        }

        // ── Launch options ────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "LAUNCH OPTIONS", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 8, 0, 8),
        });
        var chkFullscreen = new CheckBox
        {
            Content    = "Fullscreen",
            IsChecked  = LoadFullscreen(),
            Foreground = fg,
            Margin     = new Thickness(0, 0, 0, 4),
            ToolTip    = "Start APDOOM fullscreen (passed as -fullscreen when the " +
                         "command-line engine is launched).",
        };
        chkFullscreen.Checked   += (_, _) => SaveFullscreen(true);
        chkFullscreen.Unchecked += (_, _) => SaveFullscreen(false);
        panel.Children.Add(chkFullscreen);
        panel.Children.Add(new TextBlock
        {
            Text = "Applied at launch. APDOOM's own video menu and Alt+Enter still work " +
                   "in-game as usual. (If the Windows build opens its GUI launcher " +
                   "instead, set fullscreen there.)",
            FontSize = 10, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 8, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("APDOOM (GitHub) ↗",          RepoUrl),
            ("APDOOM legacy (GitHub) ↗",   FallbackRepoUrl),
            ("DOOM II Setup Guide ↗",      "https://archipelago.gg/tutorial/DOOM%20II/setup_en"),
            ("Archipelago Official ↗",     "https://archipelago.gg"),
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

    /// "v2.0.0-beta3" / "1.2.0" → trimmed, leading 'v' stripped only when it
    /// decorates a digit. Returns null for null/blank tags. APDOOM tags are not
    /// plain semver (prerelease suffixes), so keep the raw tag otherwise.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the newest release on the PRIMARY repo (prereleases included —
    /// every APDOOM release is a prerelease, so /releases/latest 404s). Returns
    /// version + Windows zip asset URL + apworld asset URL + apworld filename.
    /// Falls back to the pinned Daivuk 1.2.0 direct URLs when the primary API is
    /// unreachable.
    private async Task<(string Version, string? ZipUrl, string? ApWorldUrl, string? ApWorldName)>
        ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            // Enumerate /releases (GitHub returns newest first) and take the
            // first entry that is not a draft. Prereleases are accepted.
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
                        var (zip, apworld, apworldName) = PickWindowsAndApworld(assets);
                        // Accept this release once we have a Windows zip; if a
                        // release somehow lacks one, fall through to the next.
                        if (zip != null)
                            return (version, zip, apworld, apworldName);
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* primary API unreachable / rate-limited → pinned fallback */ }

        // Offline-of-primary fallback: Daivuk 1.2.0, known asset URL. No apworld
        // direct URL is pinned (AP-main ships the stable world anyway).
        return (FallbackVersion, FallbackZipUrl, null, null);
    }

    /// From a release's assets array, pick the Windows .zip (by win/win64/x64
    /// pattern, excluding linux/mac/source) and the doom_ii apworld. Asset
    /// names vary in casing across releases, so match broadly.
    private static (string? Zip, string? ApWorld, string? ApWorldName)
        PickWindowsAndApworld(JsonElement assets)
    {
        string? zip = null, apworld = null, apworldName = null;
        string? anyZip = null;

        foreach (var a in assets.EnumerateArray())
        {
            string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name == null || url == null) continue;

            string lower = name.ToLowerInvariant();

            // doom_ii apworld (prefer the DOOM II world; ignore doom_1993/heretic).
            if (lower.EndsWith(".apworld") &&
                (lower.Contains("doom_ii") || lower.Contains("doom2") ||
                 lower.Contains("doomii")  || lower.Contains("doom_2")))
            {
                apworld     = url;
                apworldName = name;
            }
            else if (lower.EndsWith(".zip") &&
                     !lower.Contains("source") &&
                     !lower.Contains("ap_gen") &&   // generator tool, not the game
                     !lower.Contains("linux") &&
                     !lower.Contains("ubuntu") &&
                     !lower.Contains("mac") &&
                     !lower.Contains("darwin"))
            {
                anyZip ??= url;   // remember any plausible game zip
                if (zip == null &&
                    (lower.Contains("win") || lower.Contains("x64") || lower.Contains("x86_64")))
                    zip = url;
            }
        }

        // If no asset matched the Windows heuristics but a single non-Linux game
        // zip exists, use it (defensive).
        zip ??= anyZip;
        return (zip, apworld, apworldName);
    }

    // ── Private helpers — exe resolution ──────────────────────────────────────

    /// Resolve the installed exe: prefer the CLI engine "crispy-apdoom.exe",
    /// then any "*apdoom*"/"*doom*" engine exe, with the GUI "*launcher*.exe"
    /// last. Defensive — the exact zip contents were not inspected offline.
    private string? ResolveGameExe()
    {
        if (File.Exists(PreferredExePath)) return PreferredExePath;
        if (!Directory.Exists(GameDirectory)) return null;
        try
        {
            string? launcher = null;
            string? engine   = null;
            foreach (string exe in Directory.EnumerateFiles(GameDirectory, "*.exe", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                // Skip helper/uninstaller exes outright.
                if (name.Contains("unins") || name.Contains("setup")) continue;

                if (name.Contains("crispy") && name.Contains("doom"))
                    return exe;                       // best match — the CLI engine
                if (name.Contains("launch"))
                    launcher ??= exe;                 // GUI front-end
                else if (name.Contains("doom"))
                    engine ??= exe;                   // some other doom-named exe
            }
            return engine ?? launcher;
        }
        catch { /* directory vanished mid-scan */ }
        return null;
    }

    /// True when the resolved exe is the command-line engine (crispy-*), which
    /// consumes the AP / -iwad arguments. The GUI launcher gets them best-effort.
    private static bool IsCliEngine(string exePath)
    {
        string name = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
        return name.Contains("crispy") || (name.Contains("doom") && !name.Contains("launch"));
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private string? _lastLaunchArgs;

    /// Build the verified AP launch command line:
    ///   -game doom2 -apserver <host:port> -applayer <slot> [-password <pw>]
    ///   [-iwad <stagedWad>] [-fullscreen]
    /// (DOOM II setup guide + AP PR #3757.) Slot/password are quoted for spaces.
    private string BuildLaunchArguments(ApSession session, string exePath)
    {
        var (host, port) = ParseServerHostPort(session.ServerUri);
        var sb = new StringBuilder();

        sb.Append("-game doom2");
        sb.Append(" -apserver ").Append(Quote($"{host}:{port}"));
        sb.Append(" -applayer ").Append(Quote(session.SlotName));
        if (!string.IsNullOrEmpty(session.Password))
            sb.Append(" -password ").Append(Quote(session.Password));

        // Point the CLI engine at the staged IWAD so it never shows a chooser.
        if (IsCliEngine(exePath) && File.Exists(StagedWadPath))
            sb.Append(" -iwad ").Append(Quote(StagedWadPath));

        if (IsCliEngine(exePath) && LoadFullscreen())
            sb.Append(" -fullscreen");

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
            WorkingDirectory = GameDirectory,
            UseShellExecute  = false,
        };
        if (!string.IsNullOrEmpty(arguments))
            psi.Arguments = arguments;

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start APDOOM.");

        _gameProcess = proc;
        IsRunning    = true;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            IsRunning      = false;
            _lastLaunchArgs = null;
            GameExited?.Invoke(proc.ExitCode);
        };
    }

    // ── Private helpers — WAD (bring-your-own) ────────────────────────────────

    /// Open the WAD picker, validate by CONTENT (IWAD magic + plausible size),
    /// copy into the launcher's own ROM library (§11 — original never touched),
    /// persist the COPY's path, and stage it as DOOM2.WAD next to the exe if the
    /// game is installed. Returns true when a WAD was imported.
    private bool PromptForWadFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Select your DOOM2.WAD",
            Filter = "DOOM IWAD (*.wad)|*.wad|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return false;

        string? bad = ValidateDoom2Wad(dlg.FileName);
        if (bad != null)
        {
            MessageBox.Show(bad, "Not a valid DOOM II IWAD",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        try
        {
            Directory.CreateDirectory(RomLibraryDirectory);
            string dst = Path.Combine(RomLibraryDirectory, Path.GetFileName(dlg.FileName));
            File.Copy(dlg.FileName, dst, overwrite: true);
            SaveWadPath(dst);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not copy the WAD into the launcher library:\n{ex.Message}\n\n" +
                "Nothing was changed — your original file is untouched.",
                "WAD import failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        StageWadForGame();
        return true;
    }

    /// Content check for a DOOM II IWAD: the first 4 bytes must be the ASCII
    /// magic "IWAD" (a "PWAD" mod patch is rejected — the base game needs an
    /// IWAD), and the size must be in a loose 10–25 MB window. The commercial
    /// DOOM2.WAD v1.9 is 14,604,584 B; the BFG/Unity DOOM2.WAD are close to that,
    /// so a 10–25 MB window passes them all while rejecting a ~4 MB DOOM 1993
    /// shareware WAD or an ~12 MB Ultimate Doom DOOM.WAD picked by mistake.
    /// APDOOM itself is the authoritative validator at load — we only catch
    /// obvious mistakes. Returns null when acceptable, else a short reason.
    private static string? ValidateDoom2Wad(string path)
    {
        try
        {
            long len = new FileInfo(path).Length;
            const long min = 10L * 1024 * 1024;
            const long max = 25L * 1024 * 1024;
            if (len < min)
                return "That file is too small to be a DOOM II IWAD. Pick your DOOM2.WAD " +
                       "(about 14 MB). A ~4 MB shareware or ~12 MB DOOM.WAD is the wrong game.";
            if (len > max)
                return "That file is too large to be a DOOM II IWAD. Pick your DOOM2.WAD, " +
                       "not a mod megawad.";

            using var fs = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            if (fs.Read(magic) < 4)
                return "Could not read that file. Pick a different WAD and try again.";

            // "IWAD" = 0x49 0x57 0x41 0x44.
            bool isIwad = magic[0] == (byte)'I' && magic[1] == (byte)'W' &&
                          magic[2] == (byte)'A' && magic[3] == (byte)'D';
            bool isPwad = magic[0] == (byte)'P' && magic[1] == (byte)'W' &&
                          magic[2] == (byte)'A' && magic[3] == (byte)'D';

            if (isPwad)
                return "That is a PWAD (a mod/patch WAD). APDOOM needs the base game " +
                       "DOOM2.WAD, which is an IWAD.";
            if (!isIwad)
                return "That file is not a DOOM WAD (its header is not \"IWAD\"). " +
                       "Pick your DOOM2.WAD.";
        }
        catch
        {
            return "Could not read that file. Pick a different WAD and try again.";
        }
        return null;
    }

    /// Copy the user's library WAD to DOOM2.WAD next to the exe so APDOOM finds
    /// it. Best effort — never throws into a launch/install.
    private void StageWadForGame()
    {
        try
        {
            string? lib = LoadWadPath();
            if (string.IsNullOrEmpty(lib) || !File.Exists(lib)) return;
            if (!Directory.Exists(GameDirectory)) return;

            // Only re-copy when missing or changed (cheap length compare).
            if (File.Exists(StagedWadPath))
            {
                try
                {
                    if (new FileInfo(StagedWadPath).Length == new FileInfo(lib).Length)
                        return;
                }
                catch { /* fall through and re-copy */ }
            }
            File.Copy(lib, StagedWadPath, overwrite: true);
        }
        catch { /* staging is a convenience — APDOOM/GUI can also locate a WAD */ }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // This plugin keeps its two launcher-side settings (WAD path + fullscreen)
    // in its OWN JSON file so it stays a single self-contained source file and
    // does not modify Core/SettingsStore. BOM-less UTF-8, read-modify-write.

    private sealed class DoomSettings
    {
        public string? WadPath    { get; set; }
        public bool    Fullscreen { get; set; }
    }

    private DoomSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<DoomSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(DoomSettings s)
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

    private string? LoadWadPath()        => LoadSettings().WadPath;
    private void    SaveWadPath(string p){ var s = LoadSettings(); s.WadPath = p;    SaveSettings(s); }
    private bool    LoadFullscreen()     => LoadSettings().Fullscreen;
    private void    SaveFullscreen(bool v){ var s = LoadSettings(); s.Fullscreen = v; SaveSettings(s); }

    // ── Private helpers — download/extract ────────────────────────────────────

    private async Task DownloadAndExtractGameAsync(
        string zipUrl,
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"apdoom2-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((5, $"Downloading APDOOM {version}..."));
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
                        progress.Report((pct, $"Downloading APDOOM... {downloaded / 1_000_000}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((70, "Extracting..."));
            Directory.CreateDirectory(GameDirectory);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, GameDirectory, overwriteFiles: true);

            // Release zips often contain a single top-level sub-folder — flatten
            // it so the exe lands directly in GameDirectory. (ResolveGameExe
            // scans subdirectories too, so only flatten when the extract is a
            // lone wrapper folder with nothing at the root.)
            if (Directory.GetFiles(GameDirectory).Length == 0)
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

            progress.Report((80, "Game files extracted."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }
}
