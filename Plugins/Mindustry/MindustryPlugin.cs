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

// The launcher project sets BOTH UseWPF and UseWindowsForms=true, so several UI
// type names (Color, Brushes, Button, TextBox, HorizontalAlignment) collide
// between WPF and WinForms. The project's GlobalUsings.cs already aliases each
// of these to its WPF type globally, so this file relies on those — no local
// aliases (a local alias duplicating a global one is CS1537).

namespace LauncherV2.Plugins.Mindustry;

// ═══════════════════════════════════════════════════════════════════════════════
// MindustryPlugin — install / update / launch for "Mindustry" played through
// the Archipelago-integrated Mindustry fork by JohnMahglass. This is a NATIVE
// "ConnectsItself" integration: the forked Java game EXE contains its own full
// Archipelago client (io.github.archipelagomw.Client) and connects to the AP
// server directly — no emulator, no Lua bridge, no launcher-held ApClient.
//
// ── HONEST REALITY CHECK (2026-06-14, verified online) ───────────────────────
//
//   * AP WORLD GAME STRING: "Mindustry" — verified in APClient.java:
//       this.setGame("Mindustry");
//     and in the apworld __init__.py:
//       game: str = "Mindustry"
//     GameId here = "mindustry".
//
//   * APWORLD REPO: JohnMahglass/Archipelago-Mindustry (Python apworld).
//   * CLIENT REPO:  JohnMahglass/Mindustry-Archipelago-Randomizer (Java fork).
//     Latest release as of research: v0.5.1 (2026-06-04).
//     Windows asset: "Windows_Mindustry_0_5_1.zip" (~149 MB).
//     The zip contains: Mindustry-Archipelago.exe, jre/, and supporting files.
//     This is a SELF-CONTAINED download — it bundles its own JRE (no Java
//     install required from the user). It is NOT a mod on the Steam version.
//
//   * STEAM: Mindustry (appid 1127400, by Anuken) is a separate release.
//     The AP fork is a standalone download from GitHub, NOT related to the
//     Steam or itch.io release. This plugin does NOT touch the Steam version.
//
//   * CONNECTION is configured IN-GAME: Settings → Archipelago →
//     "Connection options" tab. The game stores credentials via Arc's
//     Core.settings (an internal binary/JSON data store co-located with the
//     Mindustry-Archipelago.exe). There is NO external config file that this
//     launcher can pre-write before launch (verified: the settings are written
//     by the game's own ArchipelagoDialog / APClient.java after the user hits
//     "Apply changes"). There is also a chat command:
//       /connect [address]:[port] [SlotName]
//     but that is typed inside the game's own chat — the launcher cannot send
//     it from outside. So this plugin does NOT prefill any connection config.
//     The Settings panel clearly shows the server/slot/password the user needs
//     to enter in-game, so they can copy-paste.
//
//   * ConnectsItself = true — the Java client owns the AP slot connection.
//     The launcher must NOT hold its own ApClient on the same slot while the
//     game is running (AP servers allow one connection per slot and will kick
//     the older one).
//
//   * SupportsStandalone = true — the fork runs normally as a full Mindustry
//     game even without an AP server. The user can launch it to explore the
//     game before connecting to a multiworld.
//
//   * INSTALL = download + extract. The zip is extracted flat into GameDirectory
//     (Games/Mindustry under the launcher base dir). The exe lives at the root
//     of the extract. Single-subdir zips (where everything is under one folder
//     inside the zip) are flattened automatically.
//
//   * VERSION is read from the GitHub release tag (e.g. "v0.5.1" → "0.5.1")
//     and stamped into this plugin's sidecar (Games/ROMs/mindustry/
//     mindustry_launcher.json) on install so the tile can show it. There is no
//     version file inside the extract itself to read.
//
//   * GITHUB API: releases endpoint for the client repo. Pinned fallback:
//     v0.5.1 in case the API is unreachable.
//
// ── WHAT THIS PLUGIN DOES ─────────────────────────────────────────────────────
//   1. DETECT install: GameDirectory contains Mindustry-Archipelago.exe and/or
//      a jre/ subdirectory. InstalledVersion from the stamped sidecar.
//   2. INSTALL/UPDATE: download Windows_Mindustry_*.zip from GitHub releases,
//      extract flat into GameDirectory, stamp the version.
//   3. LAUNCH: run Mindustry-Archipelago.exe from GameDirectory.
//      ConnectsItself = true: the user enters AP credentials in-game.
//      The Settings panel shows the session credentials for copy-paste.
//   4. STOP: kill the process if we own it.
//   5. NEWS: parse GitHub release notes for the client repo.
//   6. SETTINGS PANEL: shows detected install path, in-game connection
//      instructions, and the session credentials for copy-paste (set after
//      LaunchAsync is called).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class MindustryPlugin : IGamePlugin
{
    // ── GitHub release coordinates ────────────────────────────────────────────

    private const string GH_OWNER = "JohnMahglass";
    private const string GH_REPO  = "Mindustry-Archipelago-Randomizer";
    private static readonly string RepoUrl =
        $"https://github.com/{GH_OWNER}/{GH_REPO}";
    private static readonly string GhReleasesLatestUrl =
        $"https://api.github.com/repos/{GH_OWNER}/{GH_REPO}/releases/latest";
    private static readonly string GhReleasesUrl =
        $"https://api.github.com/repos/{GH_OWNER}/{GH_REPO}/releases";

    private const string SetupGuideUrl =
        "https://archipelago.gg/tutorial/Mindustry/setup/en";
    private const string ApworldRepoUrl =
        "https://github.com/JohnMahglass/Archipelago-Mindustry";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Pinned fallback — v0.5.1, verified 2026-06-14.
    private const string FallbackVersion  = "0.5.1";
    private const string FallbackZipName  = "Windows_Mindustry_0_5_1.zip";
    private static readonly string FallbackZipUrl =
        $"{RepoUrl}/releases/download/v{FallbackVersion}/{FallbackZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } },
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "mindustry";
    public string DisplayName => "Mindustry";
    public string Subtitle    => "Native Java client · Archipelago fork";

    /// Verified in APClient.java: this.setGame("Mindustry")
    public string ApWorldName => "Mindustry";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "mindustry.png");

    public string ThemeAccentColor => "#FF7624";   // Mindustry orange

    public string[] GameBadges => new[] { "Self-contained", "ConnectsItself" };

    public string Description =>
        "Mindustry is a factory-building and tower defense game set across " +
        "multiple planets. In the Archipelago fork by JohnMahglass, most " +
        "research-tree nodes are replaced by location checks, and unlocking " +
        "technology is seeded into the multiworld. The integration is a " +
        "self-contained Windows build — it bundles its own Java runtime so " +
        "no Java install is required. The game connects to the Archipelago " +
        "server itself: after launching, go to Settings → Archipelago, enter " +
        "your server address, slot name, and password, and press Connect. " +
        "Two planets are supported: Serpulo and Erekir, each with its own " +
        "research tree and victory condition.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    public bool IsInstalled => FindGameExe() != null;
    public bool IsRunning   { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where the Mindustry AP fork is installed. The sidecar JSON and any
    /// working files also live under this tree.
    public string GameDirectory { get; } =
        Path.Combine(AppContext.BaseDirectory, "Games", "Mindustry");

    private string SidecarDir  =>
        Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SidecarPath =>
        Path.Combine(SidecarDir, "mindustry_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process?    _gameProcess;
    private ApSession?  _lastSession;   // held so the Settings panel can show creds

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The Java client owns the AP connection — the launcher relays nothing.
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
            InstalledVersion = FindGameExe() != null
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
                await GitHubHelper.FetchLatestTagAsync(GH_OWNER, GH_REPO, ct));
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
        progress.Report((2, "Checking the latest Mindustry Archipelago release..."));
        var (version, zipUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the Mindustry Archipelago Windows download on GitHub. " +
                "Check your internet connection or download manually from: " + RepoUrl +
                "/releases");

        await DownloadAndExtractAsync(zipUrl, version, progress, ct);

        WriteStampedVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            $"Mindustry Archipelago {version} installed. " +
            "Launch the game and go to Settings → Archipelago to connect."));
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
        // ConnectsItself = true: the Java game client connects to the AP server
        // directly. There is no command-line arg or external config file to
        // prefill (verified: connection is entered in Settings → Archipelago
        // inside the game). We store the session so the Settings panel can
        // display it for the user to copy-paste.
        _lastSession = session;
        StartMindustry();
        return Task.CompletedTask;
    }

    /// The AP fork runs as a normal game without any AP connection.
    public bool SupportsStandalone => true;

    /// The Java client owns the slot — the launcher must NOT hold its own
    /// ApClient on the same slot while the game is running.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        _lastSession = null;
        StartMindustry();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (the Java client handles everything) ────────────────

    public Task ReceiveItemsAsync(
        ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var accent  = new SolidColorBrush(Color.FromRgb(0xFF, 0x76, 0x24));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Honesty note ──────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text =
                "Mindustry Archipelago is a self-contained Windows download — " +
                "it bundles its own Java runtime and does not need the Steam or " +
                "itch.io version of Mindustry. Connection to the Archipelago server " +
                "is configured inside the game (Settings → Archipelago). The " +
                "launcher cannot pre-fill the connection; use the credentials below.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Install location ──────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "INSTALL LOCATION", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 0, 0, 6),
        });

        string? exe = FindGameExe();
        panel.Children.Add(new TextBlock
        {
            Text = exe != null
                ? "Installed: " + exe
                : "Not installed. Use the Install button on the Play tab.",
            FontSize = 11,
            Foreground = exe != null ? success : muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── In-game connection steps ───────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "HOW TO CONNECT IN-GAME", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 0, 0, 8),
        });

        foreach (string step in new[]
        {
            "1. Launch Mindustry Archipelago using the Play button.",
            "2. From the main menu, open Settings → Archipelago.",
            "3. Select the \"Connection options\" tab.",
            "4. Enter your Address (e.g. archipelago.gg:38281), Slot Name, and Password.",
            "5. Press \"Apply changes\" — the game remembers these for future sessions.",
            "6. Press \"Connect\" to join the multiworld.",
            "   Alternative: press Enter in the main menu to open chat, then type:",
            "   /connect [address]:[port] [SlotName]",
        })
        {
            panel.Children.Add(new TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
            });
        }

        // ── Session credentials (shown when launched via Play) ─────────────
        if (_lastSession != null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "YOUR SESSION CREDENTIALS", FontSize = 10,
                FontWeight = FontWeights.SemiBold, Foreground = muted,
                Margin = new Thickness(0, 14, 0, 8),
            });

            void AddCredRow(string label, string value)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal,
                                           Margin = new Thickness(0, 0, 0, 4) };
                row.Children.Add(new TextBlock
                {
                    Text = label, FontSize = 11, Foreground = muted, Width = 90,
                });
                row.Children.Add(new TextBox
                {
                    Text = value, FontSize = 11, IsReadOnly = true,
                    Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
                    Foreground  = accent,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
                    MinWidth = 260,
                });
                panel.Children.Add(row);
            }

            AddCredRow("Address:", _lastSession.ServerUri);
            AddCredRow("Slot Name:", _lastSession.SlotName);
            if (!string.IsNullOrEmpty(_lastSession.Password))
                AddCredRow("Password:", _lastSession.Password);
        }

        // ── Links ──────────────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 14, 0, 8),
        });

        foreach (var (label, url) in new (string, string)[]
        {
            ("Mindustry AP Client (GitHub) ↗",  RepoUrl),
            ("Mindustry AP World (GitHub) ↗",    ApworldRepoUrl),
            ("Mindustry Setup Guide ↗",          SetupGuideUrl),
            ("Archipelago Official ↗",            ArchipelagoSite),
        })
        {
            var btn = new Button
            {
                Content = label, HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize = 12, Margin = new Thickness(0, 0, 0, 4),
                Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor = System.Windows.Input.Cursors.Hand,
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
            string json = await _http.GetStringAsync(GhReleasesUrl, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) &&
                    d.ValueKind == JsonValueKind.String)
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

    /// Returns (version, windowsZipUrl). Falls back to pinned v0.5.1 when the
    /// GitHub API is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestReleaseAsync(
        CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GhReleasesUrl, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return (FallbackVersion, FallbackZipUrl);

            // Take the first non-prerelease release (or first release if all are
            // marked prerelease). The client repo has had releases marked as
            // "Unstable" (v0.5.0) — prefer stable ones.
            JsonElement? bestEl = null;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                bool prerelease = el.TryGetProperty("prerelease", out var pr) &&
                                  pr.ValueKind == JsonValueKind.True;
                bool draft      = el.TryGetProperty("draft",      out var dr) &&
                                  dr.ValueKind == JsonValueKind.True;
                if (draft) continue;

                if (bestEl == null || !prerelease)
                    bestEl = el;

                if (!prerelease) break; // first stable wins
            }

            if (bestEl == null) return (FallbackVersion, FallbackZipUrl);

            var release = bestEl.Value;
            string? version = release.TryGetProperty("tag_name", out var tg)
                ? NormalizeTag(tg.GetString())
                : null;

            if (version == null) return (FallbackVersion, FallbackZipUrl);

            if (release.TryGetProperty("assets", out var assets) &&
                assets.ValueKind == JsonValueKind.Array)
            {
                string? winZip = null;
                string? anyZip = null;

                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u)
                                   ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    anyZip ??= url;
                    // Prefer "Windows_Mindustry_*" or "Win_Mindustry_*"
                    if (winZip == null &&
                        (lower.StartsWith("windows_mindustry") ||
                         lower.StartsWith("win_mindustry")))
                        winZip = url;
                }

                string? chosen = winZip ?? anyZip;
                if (chosen != null)
                    return (version, chosen);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable → pinned fallback */ }

        return (FallbackVersion, FallbackZipUrl);
    }

    // ── Private helpers — install detection ───────────────────────────────────

    /// Find the game exe inside GameDirectory. Returns its full path or null.
    private string? FindGameExe()
    {
        try
        {
            if (!Directory.Exists(GameDirectory)) return null;

            // Preferred: exact name used by every release.
            string preferred = Path.Combine(GameDirectory, "Mindustry-Archipelago.exe");
            if (File.Exists(preferred)) return preferred;

            // Fuzzy fallback in case a future release renames it.
            foreach (string f in Directory.EnumerateFiles(
                GameDirectory, "Mindustry*.exe", SearchOption.TopDirectoryOnly))
                return f;
        }
        catch { /* permission / IO */ }
        return null;
    }

    // ── Private helpers — download / extract ──────────────────────────────────

    private async Task DownloadAndExtractAsync(
        string zipUrl,
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(
            Path.GetTempPath(),
            $"mindustry-ap-{version}-{Guid.NewGuid():N}.zip");

        try
        {
            progress.Report((5, $"Downloading Mindustry Archipelago {version}..."));

            using var response = await _http.GetAsync(
                zipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long total      = response.Content.Headers.ContentLength ?? -1;
            long downloaded = 0;

            await using (var src = await response.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tempZip))
            {
                var buf = new byte[81920];
                int read;
                while ((read = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, read), ct);
                    downloaded += read;
                    if (total > 0)
                    {
                        int pct = (int)(5 + 65 * downloaded / total);
                        progress.Report((pct,
                            $"Downloading... {downloaded / 1_048_576}MB / {total / 1_048_576}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((72, "Extracting..."));
            Directory.CreateDirectory(GameDirectory);

            // Extract into a temporary subdirectory first, then flatten.
            string tempExtract = Path.Combine(
                Path.GetTempPath(), $"mindustry-ap-extract-{Guid.NewGuid():N}");
            try
            {
                ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

                progress.Report((82, "Installing files..."));

                // If the zip has a single top-level subdirectory (common pattern),
                // descend into it so the exe ends up at GameDirectory root.
                string sourceRoot = tempExtract;
                string[] topItems = Directory.GetFileSystemEntries(tempExtract);
                if (topItems.Length == 1 && Directory.Exists(topItems[0]))
                    sourceRoot = topItems[0];

                // Copy/move all files from sourceRoot → GameDirectory.
                foreach (string fileSrc in Directory.EnumerateFiles(
                    sourceRoot, "*", SearchOption.AllDirectories))
                {
                    string rel     = Path.GetRelativePath(sourceRoot, fileSrc);
                    string fileDst = Path.Combine(GameDirectory, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(fileDst)!);
                    File.Copy(fileSrc, fileDst, overwrite: true);
                }
            }
            finally
            {
                try { Directory.Delete(tempExtract, recursive: true); } catch { }
            }

            progress.Report((95, "Finalizing..."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartMindustry()
    {
        string? exe = FindGameExe();
        if (exe == null)
            throw new FileNotFoundException(
                "Mindustry-Archipelago.exe not found. Use the Install button to " +
                "download and install the game first.",
                "Mindustry-Archipelago.exe");

        var psi = new ProcessStartInfo
        {
            FileName         = exe,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute  = true,
        };

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException(
                "Failed to start Mindustry-Archipelago.exe.");

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
        catch { /* not all processes expose Exited cleanly */ }
    }

    // ── Private helpers — sidecar settings ───────────────────────────────────
    // Keeps the plugin self-contained: no shared Core/SettingsStore modification.

    private sealed class MindustrySettings
    {
        public string? InstalledVersion { get; set; }
    }

    private MindustrySettings LoadSidecar()
    {
        try
        {
            if (File.Exists(SidecarPath))
            {
                string txt = File.ReadAllText(SidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<MindustrySettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSidecar(MindustrySettings s)
    {
        try
        {
            Directory.CreateDirectory(SidecarDir);
            File.WriteAllText(
                SidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { }
    }

    private string? ReadStampedVersion()
    {
        string? v = LoadSidecar().InstalledVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private void WriteStampedVersion(string v)
    {
        var s = LoadSidecar();
        s.InstalledVersion = v;
        SaveSidecar(s);
    }
}
