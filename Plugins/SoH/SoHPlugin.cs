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

namespace LauncherV2.Plugins.SoH;

// ═══════════════════════════════════════════════════════════════════════════════
// SoHPlugin — install / update / launch for "The Legend of Zelda: Ocarina of
// Time (Ship of Harkinian)" — a native PC port of OoT with a BUILT-IN
// Archipelago client.
//
// REALITY CHECK (2026-06-12) — full details with sources in
// Research_V2/EMULATOR_MATRIX_2026-06-12.md §4
// ─────────────────────────────────────────────────────────────────────────────
// Ship of Harkinian is NOT an emulator integration. It is a from-scratch C/C++
// re-implementation of OoT ("Shipwright"). The Archipelago support lives in a
// SEPARATE build from HarbourMasters/Archipelago-SoH (latest tagged release:
// "SoH Archipelago 1.2.1", 2026-02-19, an apworld-only patch over the 1.0.0
// Windows/Linux/Mac/Steam-Deck builds).
//
// HOW IT CONNECTS (verified via setup/community docs):
//   * The game has its OWN native AP client: choose "Archipelago" on the
//     quest-select screen; the ESC menu has a server console, connection
//     settings, and a Death Link toggle. The save file is bound to the slot
//     and AUTO-RECONNECTS when loaded — identical to our OpenTTD-fork
//     ConnectsItself pattern. AP servers allow one connection per slot, so the
//     launcher must NOT hold its own ApClient on the same slot while SoH runs
//     (the two would kick each other off forever) — hence ConnectsItself=true.
//   * Server side needs Archipelago 0.6.7+ and the SoH apworld from the same
//     release page (oot_soh.apworld). The launcher fetches it next to the
//     install so the user can drop it into custom_worlds.
//
// GAME DATA — BRING-YOUR-OWN-ROM (§11):
//   SoH does not ship Nintendo assets. On first launch it generates an OTR
//   archive FROM THE USER'S OWN Ocarina of Time ROM (the same stance as our
//   ROM-library policy). SoH prompts for the ROM itself; this plugin just
//   pre-stages a copy of the user's ROM next to soh.exe so the first-run
//   extractor finds it. Generating the OTR is SoH's own job — we do NOT try to
//   reproduce it, and the install is HONEST about that: it shows a one-time
//   setup note ("On first launch, Ship of Harkinian will ask for your Ocarina
//   of Time ROM to extract game assets").
//
// WHAT THIS PLUGIN DOES (V2.0)
// ────────────────────────────
//   1. Installs/updates the AP build from HarbourMasters/Archipelago-SoH GitHub
//      releases (latest tag, pinned 1.2.1 fallback when the API is unreachable).
//   2. Downloads the release's SoH apworld next to the install (best effort) so
//      the user can drop it into Archipelago's custom_worlds folder.
//   3. Lets the user point at their OoT ROM; the launcher copies it into its
//      own Games/ROMs/<GameId>/ tree AND stages a copy next to soh.exe for the
//      first-run OTR generator. The user's original file is never modified.
//   4. On AP launch, pre-writes the session's host/port/slot/password into
//      shipofharkinian.json (the CVar store next to soh.exe) so the in-game AP
//      menu is pre-filled, then launches soh.exe. Plain non-AP play works
//      identically (SupportsStandalone = true).
//   5. ConnectsItself semantics — the launcher tracks process lifetime only,
//      no pipes, no Lua. The AP server side sees the slot connect.
//
// DEFENSIVE / UNVERIFIED DETAILS (flagged per the matrix, "verify at build
// time"):
//   * RELEASE ASSET NAME: the exact Windows zip filename for the AP fork was
//     NOT verified offline. ResolveLatestReleaseAsync therefore picks the
//     release's first Windows .zip asset by pattern (win/windows/x64/zip),
//     with a pinned tag fallback. Re-verify the asset name on a fork bump.
//   * shipofharkinian.json CVar KEYS for AP host/port/slot were NOT verified
//     against a real file. The upstream store is a flat JSON CVar map (e.g.
//     "gfxbackend", window/audio/controller keys). We write the AP fields
//     DEFENSIVELY into a "CVars" object under several plausible key names and
//     leave any existing keys untouched (read-modify-write). If the fork
//     ignores them, the in-game client still works — the player types the
//     values once and the slot-bound save remembers them (the matrix's
//     documented fallback). The post-install note states this.
//   * EXE NAME assumed "soh.exe" (per the matrix). If a release ships a
//     differently named exe, GameExePath resolution falls back to the first
//     "*soh*.exe" / "*harkinian*.exe" in the install root.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SoHPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GITHUB_OWNER = "HarbourMasters";
    private const string GITHUB_REPO  = "Archipelago-SoH";
    private const string RepoUrl      = $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases";
    private const string GH_RELEASES_LATEST_URL = $"{GH_RELEASES_URL}/latest";

    private const string ShipWebsite = "https://www.shipofharkinian.com/";

    /// Pinned fallback release used when the GitHub API is unreachable.
    /// "SoH Archipelago 1.2.1", 2026-02-19 (matrix §4). Tag format unverified
    /// offline — the API path is the normal route, this is only the safety net.
    private const string FallbackVersion = "1.2.1";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30), // SoH builds are large
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    /// Installed-version stamp, written after a successful install.
    private const string VersionFileName = "soh_ap_version.dat";

    /// OoT ROM accepted by SoH's OTR generator. SoH supports the common US/EU
    /// dumps in .z64 / .n64 / .v64 byte orders — accept all three by extension;
    /// SoH itself validates the dump at extraction time (we do not gatekeep on a
    /// single MD5 because SoH knows several valid masters).
    private static readonly string[] RomExtensions = { ".z64", ".n64", ".v64" };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    /// Matches the catalog entry id so the store row and this plugin line up.
    public string GameId      => "the_legend_of_zelda_ocarina_of_time_ship_of_harkinian";
    public string DisplayName => "The Legend of Zelda: Ocarina of Time (Ship of Harkinian)";
    public string Subtitle    => "Native PC port · built-in Archipelago";

    /// AP world name as registered upstream — matches the catalog entry's
    /// ap_world_name. (The matrix notes the world is distinct from the N64
    /// BizHawk OoT world: different item/location pools, different YAML.)
    public string ApWorldName => "The Legend of Zelda: Ocarina of Time (Ship of Harkinian)";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets",
                     "the_legend_of_zelda_ocarina_of_time_ship_of_harkinian.png");

    public string ThemeAccentColor => "#1E6B3A";   // Kokiri green
    public string[] GameBadges     => new[] { "Requires OoT ROM" };

    public string Description =>
        "Ship of Harkinian is a native PC port of The Legend of Zelda: Ocarina " +
        "of Time, built from a decompilation of the original game. This is the " +
        "Archipelago build: it ships its own in-game multiworld client, so you " +
        "pick \"Archipelago\" on the quest-select screen and connect from the " +
        "in-game menu — no emulator, no Lua bridge. Ship of Harkinian needs an " +
        "Ocarina of Time ROM (your own copy) to extract game assets the first " +
        "time it runs. Modern PC features come along for free: widescreen, high " +
        "frame rates, gamepad support, and quality-of-life enhancements.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }
    public bool    IsInstalled      => ResolveGameExe() != null;
    public bool    IsRunning        { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Root folder where the Archipelago SoH build is installed. Kept SEPARATE
    /// from any vanilla SoH the user has (the AP build is intentionally its own
    /// install — matrix §4).
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "ShipOfHarkinian");

    /// Preferred exe name (matrix). Resolution falls back to a fuzzy match.
    private string PreferredExePath => Path.Combine(GameDirectory, "soh.exe");

    /// The SoH CVar store — a flat JSON file next to soh.exe (Windows).
    private string ConfigPath => Path.Combine(GameDirectory, "shipofharkinian.json");

    /// Where the release's SoH apworld is saved for the user to copy into
    /// Archipelago's custom_worlds folder.
    private string ApWorldLocalPath
    {
        get
        {
            string? name = _apWorldFileName;
            return Path.Combine(GameDirectory,
                string.IsNullOrEmpty(name) ? "oot_soh.apworld" : name);
        }
    }

    private string VersionFilePath => Path.Combine(GameDirectory, VersionFileName);

    /// The launcher's own ROM library copy of the user's OoT ROM (§11).
    private string RomLibraryDirectory
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    /// Filename of the apworld asset seen on the resolved release (so the saved
    /// copy keeps the upstream name). null until a release is resolved.
    private string? _apWorldFileName;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // SoH's native AP client reports checks/items/goal to the AP server itself —
    // the launcher relays nothing. These exist for interface compatibility.
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
            string json = await _http.GetStringAsync(GH_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            AvailableVersion = doc.RootElement.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
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
        progress.Report((2, "Checking latest Ship of Harkinian (Archipelago) release..."));
        var (version, zipUrl, apworldUrl, apworldName) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;
        _apWorldFileName = apworldName;

        // 2. Already current? (idempotent fast path)
        if (IsInstalled
            && File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100, $"Ship of Harkinian (Archipelago) {version} is up to date."));
            return;
        }

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find a Windows download for Ship of Harkinian on the " +
                "GitHub release page. Check your internet connection, or download " +
                "the build manually from " + RepoUrl + "/releases.");

        // 3. Download + extract the build.
        await DownloadAndExtractGameAsync(zipUrl, version, progress, ct);

        // 4. Fetch the apworld next to the install (best effort).
        if (apworldUrl != null)
        {
            try
            {
                progress.Report((85, "Downloading the Ship of Harkinian apworld..."));
                byte[] apworld = await _http.GetByteArrayAsync(apworldUrl, ct);
                await File.WriteAllBytesAsync(ApWorldLocalPath, apworld, ct);
                progress.Report((92, $"{Path.GetFileName(ApWorldLocalPath)} saved — copy it into Archipelago's custom_worlds folder."));
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                progress.Report((92, "Could not download the apworld — get it from the GitHub release page."));
            }
        }

        // 5. Stamp the installed version.
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        progress.Report((100,
            $"Ship of Harkinian (Archipelago) {version} ready. On first launch it " +
            "will ask for your Ocarina of Time ROM to extract game assets."));
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
                "Ship of Harkinian is not installed. Click Install Game first.",
                PreferredExePath);

        // Pre-fill the in-game Archipelago connection. DEFENSIVE: the exact
        // CVar key names were not verified offline (see header), so we merge a
        // best-guess set of keys into shipofharkinian.json without disturbing
        // existing keys. If the fork ignores them, the in-game client still
        // works and the player types the values once (slot-bound save keeps
        // them). Best effort — never blocks the launch.
        try { WriteApConnectionConfig(session); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }

        StartGameProcess(exe);
        return Task.CompletedTask;
    }

    /// SoH is a complete game — plain (non-AP) play is fully supported.
    public bool SupportsStandalone => true;

    /// SoH's native in-game AP client owns the slot connection (see header).
    /// The launcher must not connect its own ApClient to the same slot while the
    /// game runs.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        string exe = ResolveGameExe()
            ?? throw new FileNotFoundException(
                "Ship of Harkinian is not installed. Click Install Game first.",
                PreferredExePath);

        // No connection prefill — the player simply doesn't pick the
        // Archipelago quest (or connects manually later).
        StartGameProcess(exe);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        ScrubApConnectionPassword();   // plaintext credentials die with the session
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // SoH's native client receives items from the AP server directly; there
        // is nothing to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // SoH renders its own AP status in-game; no launcher HUD channel.
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
                Title            = "Select Ship of Harkinian install folder",
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
            Text       = IsInstalled ? "✓ Ship of Harkinian (Archipelago) is installed"
                                     : "Not installed (click Install in the Play tab)",
            FontSize   = 11, Foreground = IsInstalled ? success : muted,
            Margin     = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Ocarina of Time ROM ──────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "OCARINA OF TIME ROM", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 8, 0, 8),
        });

        var romRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var romBox = new TextBox
        {
            Text = SettingsStore.Load().SohRomPath ?? "", IsReadOnly = true, FontSize = 12,
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
                romBox.Text = SettingsStore.Load().SohRomPath ?? "";
        };
        DockPanel.SetDock(romBtn, Dock.Right);
        romRow.Children.Add(romBtn);
        romRow.Children.Add(romBox);
        panel.Children.Add(romRow);

        panel.Children.Add(new TextBlock
        {
            Text = "Ship of Harkinian needs your own Ocarina of Time ROM to extract " +
                   "game assets the first time it runs. The launcher copies it into its " +
                   "own folder and stages it next to soh.exe — your original file is never " +
                   "modified. You can also let Ship of Harkinian prompt for the ROM itself " +
                   "on first launch.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        if (IsInstalled && File.Exists(ApWorldLocalPath))
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"{Path.GetFileName(ApWorldLocalPath)} is saved in the install folder — " +
                       @"copy it into your Archipelago custom_worlds folder (default: " +
                       @"C:\ProgramData\Archipelago\custom_worlds) to generate multiworlds.",
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
            IsChecked  = SettingsStore.Load().SohFullscreen,
            Foreground = fg,
            Margin     = new Thickness(0, 0, 0, 4),
            ToolTip    = "Start Ship of Harkinian fullscreen (written to the install's " +
                         "own shipofharkinian.json before each launch).",
        };
        chkFullscreen.Checked   += (_, _) => { var s = SettingsStore.Load(); s.SohFullscreen = true;  SettingsStore.Save(s); };
        chkFullscreen.Unchecked += (_, _) => { var s = SettingsStore.Load(); s.SohFullscreen = false; SettingsStore.Save(s); };
        panel.Children.Add(chkFullscreen);
        panel.Children.Add(new TextBlock
        {
            Text = "Applied at launch. Ship of Harkinian's own graphics menu and Alt+Enter " +
                   "still work in-game as usual.",
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
            ("Ship of Harkinian Site ↗",          ShipWebsite),
            ("Archipelago-SoH (GitHub) ↗",         RepoUrl),
            ("Archipelago Official ↗",             "https://archipelago.gg"),
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

    // ── Private helpers ───────────────────────────────────────────────────────

    /// "v1.2.1" / "SoH Archipelago 1.2.1" → "1.2.1" when a leading 'v' is the
    /// only decoration; otherwise the tag is returned as-is (trimmed). Returns
    /// null for null/blank tags. (SoH tags are not guaranteed to be plain
    /// semver — keep the raw tag when in doubt rather than mangling it.)
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the installed exe: the preferred "soh.exe", else a fuzzy match
    /// in the install root (defensive — release exe name not verified offline).
    private string? ResolveGameExe()
    {
        if (File.Exists(PreferredExePath)) return PreferredExePath;
        if (!Directory.Exists(GameDirectory)) return null;
        try
        {
            foreach (string exe in Directory.EnumerateFiles(GameDirectory, "*.exe", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (name.Contains("soh") || name.Contains("harkinian"))
                    return exe;
            }
        }
        catch { /* directory vanished mid-scan */ }
        return null;
    }

    /// Resolve the latest release: version + Windows zip asset URL + apworld
    /// asset URL + apworld filename. Falls back to the pinned tag when offline
    /// (with no direct URLs — the API is the only reliable source of the real
    /// asset names for this fork, which weren't verified offline).
    private async Task<(string Version, string? ZipUrl, string? ApWorldUrl, string? ApWorldName)>
        ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? zip = null, apworld = null, apworldName = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();

                    if (lower.EndsWith(".apworld"))
                    {
                        apworld     = url;
                        apworldName = name;
                    }
                    // First plausible Windows zip wins. SoH Windows assets are
                    // not a verified fixed name (header), so match broadly:
                    // a .zip that mentions windows/win/x64 and is not a Linux/
                    // Mac/source archive.
                    else if (zip == null && lower.EndsWith(".zip")
                             && (lower.Contains("win") || lower.Contains("x64") || lower.Contains("x86_64"))
                             && !lower.Contains("linux") && !lower.Contains("mac")
                             && !lower.Contains("source"))
                    {
                        zip = url;
                    }
                }
                // Last resort: if nothing matched the Windows heuristics but a
                // single .zip exists, take it.
                if (zip == null)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                        string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                        if (name != null && url != null &&
                            name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                            !name.Contains("source", StringComparison.OrdinalIgnoreCase))
                        {
                            zip = url;
                            break;
                        }
                    }
                }
                return (version, zip, apworld, apworldName);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback below */ }

        // Offline fallback: we know the version but not the exact asset URLs.
        return (FallbackVersion, null, null, null);
    }

    /// Pre-stage the user's library ROM copy next to soh.exe so SoH's first-run
    /// OTR generator finds it without prompting. Best effort. SoH accepts a
    /// dropped ROM named like the standard dumps; we copy it under a canonical
    /// name SoH looks for and also keep its original name as a fallback.
    private void StageRomForExtraction()
    {
        try
        {
            string? lib = SettingsStore.Load().SohRomPath;
            if (string.IsNullOrEmpty(lib) || !File.Exists(lib)) return;
            if (!Directory.Exists(GameDirectory)) return;

            // SoH's first-run extractor scans the exe folder for an OoT ROM by
            // extension, so dropping the file in with its real extension is
            // enough — name does not need to be exact.
            string dst = Path.Combine(GameDirectory, Path.GetFileName(lib));
            if (!File.Exists(dst))
                File.Copy(lib, dst, overwrite: false);
        }
        catch { /* staging is a convenience — SoH will prompt if it's missing */ }
    }

    /// Open the ROM picker, validate by extension + size, copy into the
    /// launcher's own ROM library (§11 — original never touched), persist the
    /// COPY's path, and stage it next to soh.exe if the game is installed.
    /// Returns true when a ROM was imported.
    private bool PromptForRomFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Select your Ocarina of Time ROM",
            Filter = "Nintendo 64 ROM (*.z64;*.n64;*.v64)|*.z64;*.n64;*.v64|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return false;

        string? bad = ValidateOotRom(dlg.FileName);
        if (bad != null)
        {
            MessageBox.Show(bad, "Not a valid Ocarina of Time ROM",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        try
        {
            Directory.CreateDirectory(RomLibraryDirectory);
            string dst = Path.Combine(RomLibraryDirectory, Path.GetFileName(dlg.FileName));
            File.Copy(dlg.FileName, dst, overwrite: true);

            var s = SettingsStore.Load();
            s.SohRomPath = dst;
            SettingsStore.Save(s);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not copy the ROM into the launcher library:\n{ex.Message}\n\n" +
                "Nothing was changed — your original file is untouched.",
                "ROM import failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        StageRomForExtraction();
        return true;
    }

    /// Lightweight content check for an OoT ROM: known extension + plausible
    /// size (an N64 OoT cartridge is 32 MB; accept 16–64 MB to allow byte-order
    /// variants and avoid gatekeeping on a single MD5 — SoH itself does the
    /// authoritative validation at extraction). Returns null when acceptable,
    /// else a short human-readable reason.
    private static string? ValidateOotRom(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (Array.IndexOf(RomExtensions, ext) < 0)
            return "That file is not a Nintendo 64 ROM (expected .z64, .n64, or .v64).";

        try
        {
            long len = new FileInfo(path).Length;
            const long min = 16L * 1024 * 1024;
            const long max = 64L * 1024 * 1024;
            if (len < min || len > max)
                return "That file is the wrong size for an Ocarina of Time ROM " +
                       "(expected about 32 MB). Make sure it is the full N64 cartridge dump.";
        }
        catch
        {
            return "Could not read that file. Pick a different ROM and try again.";
        }
        return null;
    }

    /// Pre-fill the in-game Archipelago connection by merging AP fields into
    /// shipofharkinian.json. DEFENSIVE (see header): the exact CVar key names
    /// were not verified offline, so we:
    ///   * read-modify-write the existing JSON (never clobber user CVars),
    ///   * write the AP host/port/slot/password under a "CVars" object using
    ///     several plausible key spellings,
    ///   * keep the file BOM-less.
    /// If the fork uses different keys, the in-game client still works — the
    /// player enters the values once and the slot-bound save remembers them.
    private void WriteApConnectionConfig(ApSession session)
    {
        var (host, port) = ParseServerHostPort(session.ServerUri);

        Directory.CreateDirectory(GameDirectory);

        // Read the existing CVar store if present (preserve every key).
        Dictionary<string, JsonElement> root = new();
        if (File.Exists(ConfigPath))
        {
            try
            {
                string existing = File.ReadAllText(ConfigPath);
                if (!string.IsNullOrWhiteSpace(existing))
                {
                    var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existing);
                    if (parsed != null) root = parsed;
                }
            }
            catch { /* corrupt/unknown shape — start fresh rather than fail launch */ }
        }

        // SoH stores its settings under a top-level "CVars" object. Merge into a
        // mutable copy of it (or create it). We never remove existing entries.
        var cvars = new Dictionary<string, object?>();
        if (root.TryGetValue("CVars", out var cv) && cv.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in cv.EnumerateObject())
                cvars[p.Name] = JsonElementToObject(p.Value);
        }

        // Plausible AP CVar spellings — written defensively (header). Harmless
        // extras: SoH ignores CVars it doesn't recognise.
        cvars["gArchipelagoServer"]   = host;
        cvars["gArchipelagoPort"]     = port;
        cvars["gArchipelagoSlot"]     = session.SlotName;
        cvars["gArchipelagoPassword"] = session.Password ?? "";
        cvars["Archipelago.Server"]   = host;
        cvars["Archipelago.Port"]     = port;
        cvars["Archipelago.Slot"]     = session.SlotName;
        cvars["Archipelago.Password"] = session.Password ?? "";

        // §-launch fullscreen toggle, defensive key set (also a CVar).
        bool wantFs = SettingsStore.Load().SohFullscreen;
        cvars["gFullscreen"]  = wantFs;
        cvars["Fullscreen"]   = wantFs;

        // Re-assemble: keep all non-CVars top-level keys verbatim, replace CVars.
        var outRoot = new Dictionary<string, object?>();
        foreach (var kv in root)
        {
            if (kv.Key == "CVars") continue;
            outRoot[kv.Key] = JsonElementToObject(kv.Value);
        }
        outRoot["CVars"] = cvars;

        string json = JsonSerializer.Serialize(outRoot, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        File.WriteAllText(ConfigPath, json, new UTF8Encoding(false));
    }

    /// Blank the AP password CVars in shipofharkinian.json once the session
    /// ends — the plaintext room password should not outlive the session on
    /// disk. Best effort; the next AP launch rewrites the file anyway.
    private void ScrubApConnectionPassword()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            string text = File.ReadAllText(ConfigPath);
            if (string.IsNullOrWhiteSpace(text)) return;

            var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text);
            if (root == null || !root.TryGetValue("CVars", out var cv)
                             || cv.ValueKind != JsonValueKind.Object) return;

            var cvars = new Dictionary<string, object?>();
            bool changed = false;
            foreach (var p in cv.EnumerateObject())
            {
                if (p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase) &&
                    p.Value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrEmpty(p.Value.GetString()))
                {
                    cvars[p.Name] = "";
                    changed = true;
                }
                else
                {
                    cvars[p.Name] = JsonElementToObject(p.Value);
                }
            }
            if (!changed) return;

            var outRoot = new Dictionary<string, object?>();
            foreach (var kv in root)
            {
                if (kv.Key == "CVars") continue;
                outRoot[kv.Key] = JsonElementToObject(kv.Value);
            }
            outRoot["CVars"] = cvars;

            File.WriteAllText(ConfigPath,
                JsonSerializer.Serialize(outRoot, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* best effort — the next AP launch overwrites the file anyway */ }
    }

    /// Convert a JsonElement to a plain object so it round-trips through
    /// JsonSerializer.Serialize unchanged (used to preserve unknown CVars).
    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out long l) ? l : el.GetDouble(),
        JsonValueKind.True   => true,
        JsonValueKind.False  => false,
        JsonValueKind.Null   => null,
        _                    => el.Clone(),   // objects/arrays preserved as-is
    };

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

    private void StartGameProcess(string exePath)
    {
        // Make sure the user's ROM is staged for SoH's first-run extractor.
        StageRomForExtraction();

        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = exePath,
            WorkingDirectory = GameDirectory,
            UseShellExecute  = false,
        }) ?? throw new InvalidOperationException("Failed to start Ship of Harkinian.");

        _gameProcess = proc;
        IsRunning    = true;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            IsRunning = false;
            ScrubApConnectionPassword();   // session over — blank password CVars
            GameExited?.Invoke(proc.ExitCode);
        };
    }

    private async Task DownloadAndExtractGameAsync(
        string zipUrl,
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"soh-ap-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((5, $"Downloading Ship of Harkinian {version}..."));
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
                        progress.Report((pct, $"Downloading Ship of Harkinian... {downloaded / 1_000_000}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((70, "Extracting..."));
            Directory.CreateDirectory(GameDirectory);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, GameDirectory, overwriteFiles: true);

            // Release zips often contain a single top-level sub-folder — flatten
            // it so the exe lands directly in GameDirectory.
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

            progress.Report((80, "Game files extracted."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }
}
