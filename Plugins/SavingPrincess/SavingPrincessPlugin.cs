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

namespace LauncherV2.Plugins.SavingPrincess;

// ═══════════════════════════════════════════════════════════════════════════════
// SavingPrincessPlugin — install / update / launch for "Saving Princess", a
// retro run-and-gun metroidvania by BRAINOS, played through its Archipelago
// build. This is a NATIVE "ConnectsItself" integration (NOT a BizHawk / Lua
// emulator game): the patched game speaks to the AP server itself via a built-in
// GameMaker AP client (gm-apclientpp.dll), exactly like Ship of Harkinian, the
// OpenTTD Archipelago fork, APDOOM, and Celeste 64.
//
// HONESTY NOTE — this is the SoH-style GUIDED case, NOT the clean Celeste 64
// "one zip and run" case. The reasons are concrete and verified this session:
//
//   * The Archipelago support for Saving Princess is a MOD (a bsdiff4 patch over
//     the base game's GameMaker data.win, plus the gm-apclientpp.dll AP client),
//     NOT a self-contained redistributable game. The GitHub releases ship only
//     the mod overlay, not a runnable game.
//   * The base game is BRAINOS's freeware (name-your-own-price, a free download
//     exists) on itch.io — a separate download the player obtains themselves.
//   * The OFFICIAL, documented install path (setup guide + the apworld's own
//     Client.py) is: run the "Saving Princess Client" inside the Archipelago
//     launcher, which (a) extracts the game files out of the base "Saving
//     Princess.exe" (a Windows cab archive embedded in the installer exe),
//     (b) applies saving_princess_basepatch.bsdiff4 to produce the patched
//     data.win, and (c) launches the patched "Saving Princess v0_8.exe" with
//     --server / --name / --password.
//
// Reproducing that base-exe → data.win pipeline in C# (find the embedded MSCF
// cab header, shell out to Extrac32, apply a bsdiff4 patch) would be fragile and
// is explicitly the apworld client's job. So — exactly like SoH did NOT try to
// reproduce SoH's OTR generation — this plugin does NOT fake that install. It is
// honest about the two real, supported paths and supports BOTH:
//
//   PATH A (recommended, fully native): the player completes setup once via the
//     Archipelago "Saving Princess Client" (it does the extract+patch). They then
//     point THIS plugin at that finished install folder (the folder containing
//     "Saving Princess v0_8.exe"). From then on the launcher launches the patched
//     exe directly with the documented AP args — no second tool needed per play.
//
//   PATH B (convenience): this plugin downloads the mod overlay
//     (saving_princess_archipelago.zip) from the GitHub releases into the install
//     folder so those files are present, and guides the player to finish setup
//     (obtain the base game + run the Saving Princess Client once). It never
//     claims to have produced a runnable game when it has not.
//
// REALITY CHECK (2026-06-14) — facts verified this session
// ─────────────────────────────────────────────────────────────────────────────
//   * AP WORLD: game string "Saving Princess" — VERIFIED against AP-main
//     worlds/saving_princess/Constants.py (GAME_NAME = "Saving Princess") and
//     __init__.py (SavingPrincessWorld.game = GAME_NAME). World id
//     "saving_princess". required_client_version (0, 5, 0).
//
//   * MOD REPO (verified via the GitHub releases API this session):
//       LeonarthCG/saving-princess-archipelago
//     Latest NON-prerelease tag: "saving-princess-basepatch-1.1.0". Every release
//     ships exactly two assets: "saving_princess_archipelago.zip" (the overlay:
//     bsdiff4 patch + gm-apclientpp.dll + support files) and
//     "saving_princess_basepatch.bsdiff4". The apworld's Constants.py points the
//     client at the same repo's /releases and looks for the asset whose name
//     contains "saving_princess_archipelago" (DOWNLOAD_NAME). Some releases are
//     prereleases, so /releases/latest CAN 404 — we enumerate /releases and take
//     the newest non-draft (matching the apworld client, which reads index [0]).
//     "saving-princess-basepatch-1.1.0" is pinned as the offline fallback.
//
//   * HOW IT CONNECTS (VERIFIED against the official Archipelago "Saving
//     Princess" setup guide + Client.py):
//       - AUTOMATIC: the Saving Princess Client launches the game with
//             --server="host:port" --name="<slot>" --password="<pw>"
//         (Client.py builds exactly these three argv strings from an
//         archipelago:// URL). The in-game client auto-connects at the title
//         screen. THIS PLUGIN passes the same three args when it launches the
//         patched exe directly.
//       - MANUAL (always available): in-game, open the Archipelago menu (the AP
//         icon button), type server:port / slot / password into their fields,
//         press "CONNECT!". "This configuration persists through launches and
//         even updates." So even if a build ignores the CLI args, the player
//         enters the values once and the slot-bound save remembers them — the
//         documented fallback. AP allows one connection per slot, so — like
//         SoH/OpenTTD/APDOOM/Celeste64 — the launcher must NOT hold its own
//         ApClient on the same slot while the game runs: ConnectsItself = true.
//
//   * NO ROM/asset to bring INTO THE LAUNCHER: Saving Princess is freeware (the
//     base game is a free itch.io download), so there is no copyrighted ROM to
//     stage in the launcher's ROM library. The base game + the extract/patch are
//     handled by the apworld's own Saving Princess Client; this plugin does not
//     copy or modify the user's base game (§11).
//
// DEFENSIVE / UNVERIFIED DETAILS (flagged, "verify at build time"):
//   * The exact CONTENTS of saving_princess_archipelago.zip and the patched
//     game's exe name were taken from Client.py / the setup guide
//     ("Saving Princess v0_8.exe"), not inspected byte-for-byte offline.
//     ResolveGameExe() prefers "Saving Princess v0_8.exe", then any
//     "*saving*princess*" exe in the install (fuzzy), skipping the BASE installer
//     exe ("Saving Princess.exe" with no version suffix), uninstallers, and the
//     cab-extraction helper. IsInstalled is true ONLY when a patched/runnable
//     game exe is found — downloading the overlay alone does NOT flip it true,
//     because the overlay is not yet a runnable game.
//   * argv quoting follows Client.py's own form (--server="..."). We mirror it.
//   * One launcher-side setting (the completed-install folder) is kept in this
//     plugin's OWN JSON sidecar rather than modifying Core/SettingsStore — this
//     plugin is added as a single self-contained source file (same approach as
//     the Doom / Celeste 64 plugins).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SavingPrincessPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GITHUB_OWNER = "LeonarthCG";
    private const string GITHUB_REPO  = "saving-princess-archipelago";
    private const string RepoUrl      = $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases";

    /// Official Archipelago "Saving Princess" setup guide.
    private const string SetupGuideUrl =
        "https://archipelago.gg/tutorial/Saving%20Princess/setup/en";

    /// The BRAINOS freeware base game on itch.io (name-your-own-price).
    private const string ItchUrl = "https://brainos.itch.io/savingprincess";

    /// The overlay asset name the apworld client looks for (Constants.DOWNLOAD_NAME).
    private const string OverlayAssetMarker = "saving_princess_archipelago";

    // Pinned fallback — the latest non-prerelease at time of writing, with the
    // verified asset-name pattern. Used ONLY when the GitHub API is unreachable
    // so the overlay can still be fetched offline-of-the-API.
    private const string FallbackVersion = "saving-princess-basepatch-1.1.0";
    private const string FallbackOverlayName = "saving_princess_archipelago.zip";
    private static readonly string FallbackOverlayUrl =
        $"{RepoUrl}/releases/download/{FallbackVersion}/{FallbackOverlayName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    /// Installed-version stamp, written after a successful overlay download.
    private const string VersionFileName = "saving_princess_ap_version.dat";

    /// The patched, runnable game exe (verified name from Client.py / setup guide).
    private const string PatchedExeName = "Saving Princess v0_8.exe";

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "saving_princess";
    public string DisplayName => "Saving Princess";
    public string Subtitle    => "Native PC · built-in Archipelago";

    /// EXACT AP game string — verified against worlds/saving_princess/Constants.py
    /// (GAME_NAME = "Saving Princess").
    public string ApWorldName => "Saving Princess";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "saving_princess.png");

    public string ThemeAccentColor => "#C0407A";   // princess magenta
    public string[] GameBadges     => new[] { "Free" };

    public string Description =>
        "Saving Princess is a retro run-and-gun metroidvania by BRAINOS: explore a " +
        "space station crawling with rogue machines and rival bounty hunters, and " +
        "expand your arsenal as you collect upgrades to your arm cannon and armor. " +
        "This is the Archipelago build — a mod that adds a built-in multiworld " +
        "client, so weapons, upgrades and keys are shuffled into the multiworld and " +
        "the game connects to the Archipelago server itself (no emulator, no Lua " +
        "bridge). The base game is free (name-your-own-price on itch.io). Setup is " +
        "done once through Archipelago's \"Saving Princess Client\", which patches " +
        "the game; after that the launcher fills in your connection details and " +
        "launches you straight in.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// Installed == a PATCHED, runnable game exe is present (the overlay alone is
    /// not a runnable game — see header).
    public bool IsInstalled => ResolveGameExe() != null;
    public bool IsRunning   { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Root folder where the Saving Princess Archipelago install lives. Defaults
    /// to the launcher's Games tree; the user can repoint it at the folder the
    /// Saving Princess Client installed into (PATH A).
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "SavingPrincess");

    /// Preferred patched exe (verified name). Resolution falls back to a fuzzy
    /// match that excludes the base installer exe.
    private string PreferredExePath => Path.Combine(GameDirectory, PatchedExeName);

    private string VersionFilePath => Path.Combine(GameDirectory, VersionFileName);

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore
    /// so the plugin stays a single self-contained source file). Lives under the
    /// launcher's library tree for consistency with the other native plugins.
    private string SettingsSidecarPath
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId,
                        "saving_princess_launcher.json");

    private string SettingsSidecarDir => Path.GetDirectoryName(SettingsSidecarPath)!;

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // Saving Princess's native AP client reports checks/items/goal to the AP
    // server itself — the launcher relays nothing. These exist for interface
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
    // Downloads the MOD OVERLAY next to the install (so its files are present),
    // then is HONEST that final setup (obtain the free base game + run the Saving
    // Princess Client once to extract+patch) completes the install. If a patched
    // game exe is already present (PATH A — the user pointed us at a completed
    // install), this just refreshes the overlay files and stamps the version.

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 1. Resolve the latest release (pinned fallback when offline).
        progress.Report((2, "Checking latest Saving Princess (Archipelago) release..."));
        var (version, overlayUrl, _) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        // 2. Already current AND runnable? (idempotent fast path)
        if (IsInstalled
            && File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100, $"Saving Princess (Archipelago) {version} is up to date."));
            return;
        }

        if (overlayUrl == null)
            throw new InvalidOperationException(
                "Could not find the Saving Princess Archipelago download on the " +
                "GitHub release page. Check your internet connection, or download " +
                "the mod manually from " + RepoUrl + "/releases.");

        // 3. Download + extract the mod overlay into the install folder.
        await DownloadAndExtractOverlayAsync(overlayUrl, version, progress, ct);

        // 4. Stamp the overlay version.
        Directory.CreateDirectory(GameDirectory);
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = IsInstalled ? version : null;

        // 5. Honest completion message — the overlay is NOT a runnable game on
        //    its own. Tailor the wording to whether a patched exe is present.
        if (IsInstalled)
        {
            progress.Report((100,
                $"Saving Princess (Archipelago) {version} ready. Press Play to " +
                "connect — the launcher passes your AP connection automatically."));
        }
        else
        {
            progress.Report((100,
                $"Saving Princess Archipelago files {version} downloaded. To finish " +
                "setup, get the free base game from itch.io and run Archipelago's " +
                "\"Saving Princess Client\" once to patch it (it extracts and patches " +
                "the game). Then point this plugin at that install folder in Settings. " +
                "See the Setup Guide link in Settings."));
        }
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // ── AutoMod-style validation of a user-picked completed install (PATH A) ──

    /// The user located the folder the Saving Princess Client installed into.
    /// Accept it only when it contains a patched/runnable game exe. Returns null
    /// when acceptable, else a short reason so they can pick again.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick the folder the Saving Princess " +
                   "Client installed the game into.";

        try
        {
            foreach (string exe in Directory.EnumerateFiles(folder, "*.exe", SearchOption.TopDirectoryOnly))
                if (IsPatchedGameExe(exe))
                    return null;
        }
        catch
        {
            return "Could not read that folder. Pick a different one and try again.";
        }

        return "That folder does not contain the patched Saving Princess game " +
               "(\"Saving Princess v0_8.exe\"). Run Archipelago's \"Saving Princess " +
               "Client\" first to install/patch the game, then pick its install folder.";
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        string? exe = ResolveGameExe();
        if (exe == null)
            throw new FileNotFoundException(
                "Saving Princess is not installed yet. Get the free base game from " +
                "itch.io and run Archipelago's \"Saving Princess Client\" once to " +
                "patch it, then point this plugin at that install folder in Settings.",
                PreferredExePath);

        // VERIFIED connection path: pass the documented AP args exactly as the
        // Saving Princess Client does:
        //   --server="host:port" --name="<slot>" --password="<pw>"
        // The in-game client consumes them and auto-connects at the title screen.
        // Best effort — never blocks the launch. If a build ignores them, the
        // player uses the in-game Archipelago menu (slot-bound save remembers).
        string args = BuildLaunchArguments(session);
        StartGameProcess(exe, args);
        return Task.CompletedTask;
    }

    /// Saving Princess is a complete game — plain (non-AP) play is supported.
    public bool SupportsStandalone => true;

    /// Saving Princess's native in-game AP client owns the slot connection (see
    /// header). The launcher must not connect its own ApClient to the same slot
    /// while the game runs.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        string? exe = ResolveGameExe();
        if (exe == null)
            throw new FileNotFoundException(
                "Saving Princess is not installed yet. Run Archipelago's \"Saving " +
                "Princess Client\" once to patch the game, then point this plugin at " +
                "that install folder in Settings.",
                PreferredExePath);

        // No AP args — plain Saving Princess. The in-game client simply isn't told
        // to connect; the player can still connect manually from its menu later.
        StartGameProcess(exe, "");
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // AP credentials are passed on the command line (Client.py's own form),
        // not written to a plaintext file by this plugin — so there is no password
        // file to scrub. Clear the cached last-args defensively all the same.
        _lastLaunchArgs = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // Saving Princess's native client receives items from the AP server
        // directly; there is nothing to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // Saving Princess renders its own AP status in-game (the HUD connection
        // indicator); no launcher HUD channel.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xD0, 0xA0, 0x40));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Honesty header (unverified offline) ───────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "Saving Princess's Archipelago support is a mod over the free " +
                   "BRAINOS base game. Final setup is done once through Archipelago's " +
                   "\"Saving Princess Client\" (it patches the game). These details " +
                   "are taken from the official setup guide and the apworld client, " +
                   "and were not all verified offline — see the links below.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

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
                Title            = "Select your Saving Princess (Archipelago) install folder",
                InitialDirectory = Directory.Exists(GameDirectory) ? GameDirectory
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                // PATH A: if the user is pointing at a completed install, validate
                // it (a patched exe must be present) and persist it so it sticks
                // across launches. If they pick the default download folder, just
                // accept it (the overlay/version live there).
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null && Directory.Exists(picked) &&
                    !File.Exists(Path.Combine(picked, VersionFileName)))
                {
                    var go = MessageBox.Show(
                        bad + "\n\nUse this folder anyway?",
                        "No patched game found here",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (go != MessageBoxResult.Yes) return;
                }
                GameDirectory = picked;
                dirBox.Text   = picked;
                SaveInstallDir(picked);
            }
        };
        DockPanel.SetDock(dirBtn, Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new TextBlock
        {
            Text       = IsInstalled
                ? "✓ Patched Saving Princess found here — ready to play"
                : "Patched game not found here yet. Run the Saving Princess Client to " +
                  "install/patch the game, then point this at its folder.",
            FontSize   = 11, Foreground = IsInstalled ? success : muted,
            TextWrapping = TextWrapping.Wrap,
            Margin     = new Thickness(0, 6, 0, 12),
        });

        // ── Section: How setup + connection work ──────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "SETUP & CONNECTION", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 8, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "1) Get the free base game from itch.io (name-your-own-price).\n" +
                   "2) In the Archipelago launcher, run \"Saving Princess Client\" once — " +
                   "it extracts and patches the game (it can also download the mod files).\n" +
                   "3) Point the Install Directory above at that finished install folder.\n\n" +
                   "After that, pressing Play here launches the patched game and passes " +
                   "your AP server, slot and password automatically, so the in-game " +
                   "client connects at the title screen. You can also connect by hand " +
                   "from the in-game Archipelago menu — those details persist across " +
                   "launches and updates.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
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
            ("Saving Princess on itch.io (free) ↗", ItchUrl),
            ("Saving Princess Archipelago (GitHub) ↗", RepoUrl),
            ("Saving Princess Setup Guide ↗",          SetupGuideUrl),
            ("Archipelago Official ↗",                 "https://archipelago.gg"),
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

    /// Resolve the latest release: version (tag) + overlay asset URL + overlay
    /// filename. Some Saving Princess releases are prereleases, so /releases/latest
    /// can 404 — we enumerate /releases (newest first) and take the first non-draft
    /// that carries the overlay asset (mirroring the apworld client, which reads
    /// index [0]). Falls back to the pinned overlay URL when the API is unreachable.
    private async Task<(string Version, string? OverlayUrl, string? OverlayName)>
        ResolveLatestReleaseAsync(CancellationToken ct)
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
                        ? t.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(version)) continue;

                    if (rel.TryGetProperty("assets", out var assets) &&
                        assets.ValueKind == JsonValueKind.Array)
                    {
                        var (overlay, overlayName) = PickOverlay(assets);
                        if (overlay != null)
                            return (version!, overlay, overlayName);
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback below */ }

        // Offline fallback: pinned non-prerelease, known asset URL.
        return (FallbackVersion, FallbackOverlayUrl, FallbackOverlayName);
    }

    /// From a release's assets array, pick the mod overlay zip — the asset whose
    /// name contains "saving_princess_archipelago" (Constants.DOWNLOAD_NAME).
    /// Falls back to the first non-source .zip if the marker is absent.
    private static (string? Overlay, string? OverlayName) PickOverlay(JsonElement assets)
    {
        string? anyZip = null, anyZipName = null;

        foreach (var a in assets.EnumerateArray())
        {
            string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name == null || url == null) continue;

            string lower = name.ToLowerInvariant();
            if (lower.EndsWith(".zip") && lower.Contains(OverlayMarkerLower))
                return (url, name);

            if (lower.EndsWith(".zip") && !lower.Contains("source"))
            {
                anyZip     ??= url;
                anyZipName ??= name;
            }
        }
        return (anyZip, anyZipName);
    }

    private static readonly string OverlayMarkerLower = OverlayAssetMarker.ToLowerInvariant();

    // ── Private helpers — exe resolution ──────────────────────────────────────

    /// Resolve the installed PATCHED exe: prefer "Saving Princess v0_8.exe", then
    /// any "*saving*princess*" exe that is NOT the base installer exe. Defensive —
    /// the exact install contents were not inspected offline.
    private string? ResolveGameExe()
    {
        if (File.Exists(PreferredExePath)) return PreferredExePath;
        if (!Directory.Exists(GameDirectory)) return null;
        try
        {
            foreach (string exe in Directory.EnumerateFiles(GameDirectory, "*.exe", SearchOption.AllDirectories))
                if (IsPatchedGameExe(exe))
                    return exe;
        }
        catch { /* directory vanished mid-scan */ }
        return null;
    }

    /// True when an exe looks like the PATCHED, runnable Saving Princess game —
    /// not the base installer ("Saving Princess.exe" with no version suffix), an
    /// uninstaller, or the cab-extraction helper.
    private static bool IsPatchedGameExe(string exePath)
    {
        string name = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
        if (name.Contains("unins") || name.Contains("setup") ||
            name.Contains("extrac") || name.Contains("crash"))
            return false;

        // The verified patched exe name.
        if (name == "saving princess v0_8") return true;

        // Fuzzy: a "saving princess" exe that carries a version-ish marker (so we
        // don't match the bare base installer "Saving Princess").
        bool isSaving = name.Contains("saving") && name.Contains("princess");
        bool hasVersionMark = name.Contains("v0_") || name.Contains("v0.") ||
                              name.Contains(" v") || name.Contains("_v") ||
                              name.Contains("ap");
        return isSaving && hasVersionMark;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private string? _lastLaunchArgs;

    /// Build the verified AP launch command line, mirroring Client.py exactly:
    ///   --server="host:port" --name="<slot>" [--password="<pw>"]
    /// name/password are only added when present (Client.py leaves them blank
    /// otherwise). Each value is wrapped in the --key="value" form Client.py uses.
    private string BuildLaunchArguments(ApSession session)
    {
        var (host, port) = ParseServerHostPort(session.ServerUri);
        var sb = new StringBuilder();

        sb.Append("--server=").Append(QuoteEq($"{host}:{port}"));
        if (!string.IsNullOrEmpty(session.SlotName))
            sb.Append(" --name=").Append(QuoteEq(session.SlotName));
        if (!string.IsNullOrEmpty(session.Password))
            sb.Append(" --password=").Append(QuoteEq(session.Password));

        return sb.ToString();
    }

    /// Quote a value for the --key="value" argv form (always double-quoted, with
    /// embedded quotes escaped). Matches the shape Client.py emits.
    private static string QuoteEq(string value)
        => "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";

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
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? GameDirectory,
            UseShellExecute  = false,
        };
        if (!string.IsNullOrEmpty(arguments))
            psi.Arguments = arguments;

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start Saving Princess.");

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

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // Kept in this plugin's OWN JSON file so it stays a single self-contained
    // source file and does not modify Core/SettingsStore. BOM-less UTF-8.

    private sealed class SavingPrincessSettings
    {
        /// The completed-install folder the user pointed us at (PATH A), so the
        /// chosen directory survives across launcher restarts.
        public string? InstallDir { get; set; }
    }

    private SavingPrincessSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<SavingPrincessSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(SavingPrincessSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal — the setting just won't persist this time */ }
    }

    private void SaveInstallDir(string dir)
    {
        var s = LoadSettings();
        s.InstallDir = dir;
        SaveSettings(s);
    }

    /// Constructor restores a previously chosen install folder (PATH A) so the
    /// user does not have to re-point it every session.
    public SavingPrincessPlugin()
    {
        try
        {
            string? saved = LoadSettings().InstallDir;
            if (!string.IsNullOrWhiteSpace(saved) && Directory.Exists(saved))
                GameDirectory = saved!;
        }
        catch { /* fall back to the default GameDirectory */ }
    }

    // ── Private helpers — download/extract ────────────────────────────────────

    private async Task DownloadAndExtractOverlayAsync(
        string overlayUrl,
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"saving-princess-ap-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((5, $"Downloading Saving Princess Archipelago files..."));
            using var response = await _http.GetAsync(
                overlayUrl, HttpCompletionOption.ResponseHeadersRead, ct);
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
                        progress.Report((pct, $"Downloading... {downloaded / 1_000_000}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((70, "Extracting mod files..."));
            Directory.CreateDirectory(GameDirectory);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, GameDirectory, overwriteFiles: true);

            progress.Report((80, "Mod files extracted."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }
}
