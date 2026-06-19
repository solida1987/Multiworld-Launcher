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

namespace LauncherV2.Plugins.BumperStickers;

// ═══════════════════════════════════════════════════════════════════════════════
// BumperStickersPlugin — install / update / launch for "Bumper Stickers", a
// standalone match-three puzzle game by FelicitusNeko (FlixelBumpStik) whose
// downloadable build IS its own Archipelago client. This is a NATIVE
// "ConnectsItself" integration (NOT a BizHawk / Lua emulator game): the game
// speaks to the AP server itself, exactly like Celeste 64, APDOOM, Ship of
// Harkinian, and the OpenTTD Archipelago fork.
//
// This is the CLEANEST kind of native plugin — there is NO bring-your-own-asset
// gate. Bumper Stickers is FREE (a HaxeFlixel game distributed as freeware on
// GitHub and itch.io), so the Archipelago build is a fully self-contained
// Windows download: install = a single GitHub-release download + launch. It is
// therefore modelled on Plugins/Celeste64/Celeste64Plugin.cs but with one
// honest difference: the connection is entered IN-GAME, so there is nothing for
// the launcher to prefill (see below).
//
// REALITY CHECK (2026-06-14) — facts verified this session
// ─────────────────────────────────────────────────────────────────────────────
//   * REPO (verified online + the official AP "Bumper Stickers" setup guide):
//       FelicitusNeko/FlixelBumpStik — the Archipelago build of Bumper Stickers.
//     Also published on itch.io as kewliomzx.itch.io/bumpstik-ap (the same dev
//     team; KewlioMZX wrote the AP setup guide). The base game and its AP world
//     were authored by FelicitusNeko and merged into Archipelago in release
//     0.4.2 (PR #811).
//
//   * RELEASES + WINDOWS ASSET (verified against the GitHub releases API,
//     2026-06-14): every release ships THREE platform zips with the verified
//     name pattern:
//         "bumpstik-ap-<ver>-windows.zip"   (Windows  — the one we want)
//         "bumpstik-ap-<ver>-linux.zip"     (Linux)
//         "bumpstik-ap-<ver>-html5.zip"     (browser build)
//     e.g. "bumpstik-ap-0.9.0a3-windows.zip". IMPORTANT: the release TAGS use a
//     Greek small letter alpha for the alpha designation (e.g. "0.9.0α3",
//     "0.9.0α2", "0.9.0α"), and the prerelease flag is INCONSISTENT across
//     releases (some alphas are flagged prerelease, some are not). Relying on
//     /releases/latest is therefore unreliable — this plugin ENUMERATES
//     /releases (GitHub returns newest first) and takes the newest non-draft
//     entry that carries a Windows asset, exactly like the APDOOM plugin does
//     for its prerelease-only upstream. The newest at time of writing is
//     "0.9.0α3" (2025-08-19); it is pinned as the offline fallback so install
//     still works when the GitHub API is unreachable. The fallback URL hard-codes
//     the percent-encoding of the Greek alpha in the tag (α → %CE%B1).
//
//   * HOW IT CONNECTS (VERIFIED, verbatim, against the official Archipelago
//     "Bumper Stickers" setup guide — https://archipelago.gg/tutorial/
//     Bumper%20Stickers/setup_en): connection is done through an IN-GAME MENU.
//     The documented steps are:
//         1. Run BumpStikAP.exe
//         2. Select "Archipelago Mode"
//         3. "Enter your server details in the fields provided, and click
//            'Start'."
//     "The game will attempt to automatically detect whether to connect via
//     normal (WS) or secure (WSS) server, but you can specify ws:// or wss:// to
//     prioritise one or the other." There is NO documented config file and NO
//     documented command line for the AP connection — the in-game fields ARE the
//     interface. So, UNLIKE Celeste 64 (which has a verified AP.json) and APDOOM
//     (which has verified CLI args), there is NOTHING for the launcher to prefill
//     here. This plugin is HONEST about that: LaunchAsync simply launches the exe
//     and the player types the server / slot / password into the in-game
//     Archipelago Mode screen. We do NOT invent an undocumented config file or
//     CLI — that would be guesswork that could break the build. The launcher
//     still copies the connection details to the clipboard as a best-effort
//     convenience (so the player can paste rather than retype) and surfaces them
//     in the post-launch flow; the game itself owns the connection. AP servers
//     allow one connection per slot, so — like Celeste 64 / APDOOM / OpenTTD —
//     the launcher must NOT hold its own ApClient on the same slot while the game
//     runs: ConnectsItself = true.
//
//   * THE AP WORLD: game string "Bumper Stickers" — verified against AP-main
//     worlds/bumpstik/__init__.py (BumpStikWorld.game = "Bumper Stickers",
//     line 35 of the local checkout in mk64src/worlds/bumpstik/). World id
//     "bumpstik". The stable world ships with Archipelago itself; the GitHub
//     releases do NOT bundle a standalone .apworld asset (only the three
//     platform zips), so there is nothing extra to fetch — this plugin is leaner
//     than Celeste/Doom for that reason (no apworld-download machinery).
//
//   * NO ASSET GATE: the game is free, so the AP build ships a COMPLETE, playable
//     game. There is no ROM/IWAD to supply, no first-run extractor, no asset
//     library copy. Plain non-AP play is supported (SupportsStandalone = true):
//     the game has a "Classic" mode that works independently of Archipelago.
//
// DEFENSIVE / UNVERIFIED DETAILS (flagged, "verify at build time"):
//   * The exact zip CONTENTS were not inspected offline. ResolveGameExe()
//     prefers "BumpStikAP.exe" (the verified name), then any "*bumpstik*"/
//     "*bumpstik-ap*" exe in the install (fuzzy), skipping obvious helper /
//     uninstaller exes. Single-subdir extracts are flattened so the exe lands at
//     the install root.
//   * The in-game field LABELS (server / slot / password) are not quoted in the
//     guide, so this plugin makes no assumption about them — it does not type or
//     automate the menu. The clipboard convenience uses a neutral "host:port"
//     form.
//   * One launcher-side setting (the install dir is the only one that matters)
//     is enough; this plugin keeps any optional setting in its OWN JSON sidecar
//     (Games/ROMs/bumpstik/bumpstik_launcher.json) rather than modifying the
//     shared Core/SettingsStore — it is added as a single self-contained file.
//     (The Doom and Celeste 64 plugins took the same self-contained-sidecar
//     approach.)
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class BumperStickersPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GITHUB_OWNER = "FelicitusNeko";
    private const string GITHUB_REPO  = "FlixelBumpStik";
    private const string RepoUrl      = $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases";

    /// Official Archipelago "Bumper Stickers" setup guide.
    private const string SetupGuideUrl =
        "https://archipelago.gg/tutorial/Bumper%20Stickers/setup_en";

    /// Itch.io page (same dev team — also offers an in-browser build).
    private const string ItchUrl = "https://kewliomzx.itch.io/bumpstik-ap";

    // Pinned fallback — the newest release at time of writing, with the verified
    // asset-name pattern. Used ONLY when the GitHub API is unreachable so a fresh
    // install still works offline-of-the-API. The tag "0.9.0α3" contains a Greek
    // small letter alpha (U+03B1) which percent-encodes to "%CE%B1" in the URL.
    private const string FallbackVersion = "0.9.0a3";          // ASCII-friendly stamp
    private const string FallbackTagEncoded = "0.9.0%CE%B13";  // "0.9.0α3" URL-encoded
    private const string FallbackZipName = "bumpstik-ap-0.9.0a3-windows.zip";
    private static readonly string FallbackZipUrl =
        $"{RepoUrl}/releases/download/{FallbackTagEncoded}/{FallbackZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    /// Installed-version stamp, written after a successful install.
    private const string VersionFileName = "bumpstik_ap_version.dat";

    /// Verified executable name inside the Windows build.
    private const string PreferredExeName = "BumpStikAP.exe";

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "bumpstik";
    public string DisplayName => "Bumper Stickers";
    public string Subtitle    => "Native PC · built-in Archipelago";

    /// EXACT AP game string — verified against worlds/bumpstik/__init__.py
    /// (BumpStikWorld.game = "Bumper Stickers").
    public string ApWorldName => "Bumper Stickers";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "bumpstik.png");

    public string ThemeAccentColor => "#E0A030";   // warm bumper amber
    public string[] GameBadges     => new[] { "Free" };

    public string Description =>
        "Bumper Stickers is a match-three puzzle game unlike any you've seen, by " +
        "FelicitusNeko. Launch bumpers onto the field and match them in sets of " +
        "three of the same colour — how long can you go without getting jammed? " +
        "This is the Archipelago build: it ships its own built-in multiworld " +
        "client, so Treasure Bumpers and Bonus Boosters send items to your friends " +
        "and the game connects to the Archipelago server itself — no emulator, no " +
        "Lua bridge. Because the game is free, the download is the complete game: " +
        "nothing to bring, nothing to extract. Press Play, choose Archipelago Mode, " +
        "and enter your connection details on the in-game screen.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }
    public bool    IsInstalled      => ResolveGameExe() != null;
    public bool    IsRunning        { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Root folder where the Bumper Stickers Archipelago build is installed.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "BumperStickers");

    /// Preferred exe (verified name). Resolution falls back to a fuzzy match.
    private string PreferredExePath => Path.Combine(GameDirectory, PreferredExeName);

    private string VersionFilePath => Path.Combine(GameDirectory, VersionFileName);

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore
    /// so the plugin stays a single self-contained source file). Lives under the
    /// launcher's ROM-library tree for consistency with the other native plugins,
    /// even though this game brings no ROM.
    private string SettingsSidecarPath
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId,
                        "bumpstik_launcher.json");

    private string SettingsSidecarDir => Path.GetDirectoryName(SettingsSidecarPath)!;

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // Bumper Stickers' native AP client reports checks/items/goal to the AP
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
            var (version, _) = await ResolveLatestReleaseAsync(ct);
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
        progress.Report((2, "Checking latest Bumper Stickers (Archipelago) release..."));
        var (version, zipUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        // 2. Already current? (idempotent fast path)
        if (IsInstalled
            && File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100, $"Bumper Stickers (Archipelago) {version} is up to date."));
            return;
        }

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find a Windows download for Bumper Stickers on the GitHub " +
                "release page. Check your internet connection, or download the build " +
                "manually from " + RepoUrl + "/releases.");

        // 3. Download + extract the build.
        await DownloadAndExtractGameAsync(zipUrl, version, progress, ct);

        // 4. Stamp the installed version.
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        progress.Report((100,
            $"Bumper Stickers (Archipelago) {version} ready. Press Play, choose " +
            "Archipelago Mode, and enter your connection details on the in-game screen."));
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
                "Bumper Stickers is not installed. Click Install Game first.",
                PreferredExePath);

        // HONEST connection path: Bumper Stickers takes its AP connection from an
        // IN-GAME menu ("Archipelago Mode" → server fields → Start). There is no
        // documented config file or command line, so there is nothing to prefill
        // — we do not invent an undocumented interface. As a best-effort
        // convenience we copy the server address to the clipboard so the player
        // can paste it into the in-game field instead of retyping. Never blocks
        // the launch.
        try { CopyServerToClipboard(session); } catch { /* clipboard is optional */ }

        StartGameProcess(exe);
        return Task.CompletedTask;
    }

    /// Bumper Stickers has a non-AP "Classic" mode, so plain play is supported.
    public bool SupportsStandalone => true;

    /// Bumper Stickers' native in-game AP client owns the slot connection (see
    /// header). The launcher must not connect its own ApClient to the same slot
    /// while the game runs.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        string exe = ResolveGameExe()
            ?? throw new FileNotFoundException(
                "Bumper Stickers is not installed. Click Install Game first.",
                PreferredExePath);

        // No AP prefill — plain Bumper Stickers (the in-game "Classic" mode). The
        // connection lives only in the in-game Archipelago Mode screen, so there
        // is nothing on disk to clear.
        StartGameProcess(exe);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // The AP password is entered in-game and is never written to a launcher
        // file or command line, so there is no plaintext password on disk to
        // scrub. Best-effort: clear the clipboard if it still holds the server
        // address we copied, so it does not linger after the session.
        ClearServerFromClipboard();
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // Bumper Stickers' native client receives items from the AP server
        // directly; there is nothing to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // Bumper Stickers renders its own AP status in-game; no launcher HUD
        // channel.
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
                Title            = "Select Bumper Stickers install folder",
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
            Text       = IsInstalled ? "✓ Bumper Stickers (Archipelago) is installed"
                                     : "Not installed (click Install in the Play tab)",
            FontSize   = 11, Foreground = IsInstalled ? success : muted,
            Margin     = new Thickness(0, 6, 0, 12),
        });

        // ── Section: How it connects ──────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "ARCHIPELAGO CONNECTION", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 8, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Bumper Stickers connects from inside the game. Press Play, choose " +
                   "\"Archipelago Mode\", enter your server details in the fields provided, " +
                   "and click Start. The game auto-detects ws:// vs wss:// (you can type " +
                   "the prefix to force one). The launcher copies your server address to " +
                   "the clipboard when you press Play so you can paste it in — there is no " +
                   "config file to edit.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        panel.Children.Add(new TextBlock
        {
            Text = "This game is free — there is nothing to bring. The Archipelago build " +
                   "is the complete game, so install is just a download. The Bumper " +
                   "Stickers world ships with Archipelago itself, so there is no extra " +
                   "apworld to copy in.",
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
            ("Bumper Stickers (GitHub) ↗",     RepoUrl),
            ("Bumper Stickers on itch.io ↗",   ItchUrl),
            ("Bumper Stickers Setup Guide ↗",  SetupGuideUrl),
            ("Archipelago Official ↗",         "https://archipelago.gg"),
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

    /// "0.9.0α3" → kept as-is (trimmed); a leading 'v' is stripped only when it
    /// decorates a digit. Bumper Stickers tags are not plain semver (they carry a
    /// Greek-alpha prerelease marker), so we never reformat beyond that. Returns
    /// null for null/blank tags.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the newest release: version + Windows zip asset URL. Bumper
    /// Stickers' prerelease flag is inconsistent across releases and the tags use
    /// a Greek alpha, so /releases/latest is unreliable — we ENUMERATE /releases
    /// (GitHub returns newest first) and take the first non-draft entry that
    /// carries a Windows asset (prereleases accepted). Falls back to the pinned
    /// 0.9.0α3 direct URL when the API is unreachable.
    private async Task<(string Version, string? ZipUrl)>
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

        // Offline fallback: 0.9.0α3, known asset URL.
        return (FallbackVersion, FallbackZipUrl);
    }

    /// From a release's assets array, pick the Windows .zip. Verified pattern:
    /// "bumpstik-ap-<ver>-windows.zip". Match broadly on "windows"/"win"/"x64",
    /// excluding the linux / html5 / mac / source variants.
    private static string? PickWindowsZip(JsonElement assets)
    {
        string? zip = null;
        string? anyZip = null;

        foreach (var a in assets.EnumerateArray())
        {
            string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name == null || url == null) continue;

            string lower = name.ToLowerInvariant();
            if (!lower.EndsWith(".zip")) continue;
            if (lower.Contains("source")) continue;
            if (lower.Contains("html5") || lower.Contains("web")) continue;
            if (lower.Contains("linux") || lower.Contains("ubuntu")) continue;
            if (lower.Contains("mac") || lower.Contains("osx") || lower.Contains("darwin")) continue;

            anyZip ??= url;   // remember any plausible (non-excluded) game zip
            if (zip == null &&
                (lower.Contains("windows") || lower.Contains("win") || lower.Contains("x64")))
                zip = url;
        }

        // If nothing matched the Windows heuristics but a single non-Linux/web
        // game zip exists, use it (defensive).
        return zip ?? anyZip;
    }

    // ── Private helpers — exe resolution ──────────────────────────────────────

    /// Resolve the installed exe: prefer "BumpStikAP.exe", then any
    /// "*bumpstik*" exe in the install (fuzzy), skipping helper/uninstaller exes.
    /// Defensive — the exact zip contents were not inspected offline.
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
                if (name.Contains("bumpstik") || name.Contains("bumper"))
                    return exe;
            }
        }
        catch { /* directory vanished mid-scan */ }
        return null;
    }

    // ── Private helpers — clipboard convenience (best effort) ─────────────────
    // Bumper Stickers has no config file / CLI for the AP connection — it is
    // entered in-game. As a courtesy we put the server "host:port" on the
    // clipboard at launch so the player can paste it into the in-game field, and
    // clear it again at StopAsync if it is still ours. We deliberately do NOT put
    // the password on the clipboard.

    private string? _clipboardServer;

    private void CopyServerToClipboard(ApSession session)
    {
        string server = FormatServerUrl(session.ServerUri);
        if (string.IsNullOrEmpty(server)) return;

        void Set()
        {
            try { System.Windows.Clipboard.SetText(server); _clipboardServer = server; }
            catch { /* clipboard busy/unavailable — non-fatal */ }
        }

        // Clipboard access must run on an STA thread; marshal to the UI thread.
        var disp = Application.Current?.Dispatcher;
        if (disp != null && !disp.CheckAccess()) disp.Invoke(Set);
        else Set();
    }

    private void ClearServerFromClipboard()
    {
        if (_clipboardServer == null) return;
        string mine = _clipboardServer;
        _clipboardServer = null;

        void Clear()
        {
            try
            {
                // Only clear if the clipboard still holds exactly what we set, so
                // we never wipe something the user copied in the meantime.
                if (System.Windows.Clipboard.ContainsText() && System.Windows.Clipboard.GetText() == mine)
                    System.Windows.Clipboard.Clear();
            }
            catch { /* non-fatal */ }
        }

        var disp = Application.Current?.Dispatcher;
        if (disp != null && !disp.CheckAccess()) disp.Invoke(Clear);
        else Clear();
    }

    /// Normalise the launcher's server URI into a "host:port" form for the
    /// clipboard (strip ws://wss:// scheme + any path; default port 38281).
    /// Handles bare hostnames and IPv6 literals.
    private static string FormatServerUrl(string serverUri)
    {
        var (host, port) = ParseServerHostPort(serverUri);
        return host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
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

    // ── Private helpers — process ─────────────────────────────────────────────

    private void StartGameProcess(string exePath)
    {
        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = exePath,
            WorkingDirectory = GameDirectory,
            UseShellExecute  = false,
        }) ?? throw new InvalidOperationException("Failed to start Bumper Stickers.");

        _gameProcess = proc;
        IsRunning    = true;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            IsRunning = false;
            ClearServerFromClipboard();   // session over — drop our clipboard copy
            GameExited?.Invoke(proc.ExitCode);
        };
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // Kept in this plugin's OWN JSON file so it stays a single self-contained
    // source file and does not modify Core/SettingsStore. Reserved for any future
    // launcher-side toggle; the install dir is the only setting that matters
    // today, so the sidecar is currently unused beyond this scaffolding.

    private sealed class BumperStickersSettings
    {
        // Intentionally empty for now — a placeholder for future launcher-side
        // options without touching the shared store.
    }

    private BumperStickersSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<BumperStickersSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(BumperStickersSettings s)
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
            $"bumpstik-ap-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((5, $"Downloading Bumper Stickers {version}..."));
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
                        progress.Report((pct, $"Downloading Bumper Stickers... {downloaded / 1_000_000}MB"));
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
