using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

// NOTE: WPF UI types are FULLY QUALIFIED throughout this file (System.Windows.*).
// The real launcher project sets <UseWindowsForms>true</UseWindowsForms> alongside
// WPF, so the bare names Button / TextBox / Color / Brushes / MessageBox /
// FontWeights / HorizontalAlignment collide between System.Windows.Forms and
// System.Windows[.Controls/.Media] (CS0104). Qualifying every UI type avoids that.
// No file-level aliases (using X = System.Windows...) either — GlobalUsings.cs
// already imports them globally and a second file-level alias is CS1537.

namespace LauncherV2.Plugins.HighRoller;

// ════════════════════════════════════════════════════════════════════════════════
// HighRollerPlugin — install / launch for "High Roller" (ElireFeltores / E-A-V,
// 2024) a free standalone slot-machine-themed Archipelago game whose client is
// built directly into the game executable. ConnectsItself = true — the game itself
// speaks to the AP server; the launcher must NOT hold its own ApClient on this
// slot while the game runs.
//
// ── VERIFIED FACTS (2026-06-14, checked against ElireFeltores/High-Roller repo) ─
//
//   * AP GAME STRING: "High Roller"
//     Verified in world.py: class HighRollerWorld(World): ... game = "High Roller"
//     (archipelago.json: "game": "High Roller"; minimum_ap_version: "0.6.4")
//     GameId (internal / filesystem) = "high_roller".
//
//   * DISTRIBUTION: Freeware, GitHub releases only.
//     Repository: https://github.com/ElireFeltores/High-Roller
//     Release assets (verified v1.3.0, 2026-06-14):
//       "High.Roller.zip"         — the standalone game client  (~4.8 MB)
//       "high_roller.apworld"     — the AP world file
//     NOT on Steam. NOT on itch.io (no link or homepage found).
//
//   * INTEGRATION: The game client is built with its own Archipelago connection
//     logic (the release notes refer to "Client Changes" such as sound effects and
//     game-state resets). The game is a fully self-contained executable inside the
//     zip. ConnectsItself = true.
//
//   * CONNECTION METHOD: in-game.
//     No config file that can be pre-written has been identified for this client
//     (no host/port/slot/password JSON or INI in the repo or release ZIP). The
//     user enters connection details inside the game. The settings panel surfaces
//     session credentials so the user can copy them.
//
//   * GAME EXECUTABLE: inferred as "High Roller.exe" (or similar) inside the zip.
//     The launcher searches the extracted folder for any .exe to launch.
//
// ── WHAT THIS PLUGIN DOES ─────────────────────────────────────────────────────
//   1. INSTALL: download the latest "High.Roller.zip" from GitHub releases, extract
//      it into Games/HighRoller, and stamp the installed version.
//   2. UPDATE: if a newer release exists, re-download and overwrite.
//   3. LAUNCH: find and start the game exe from the extracted folder.
//      SupportsStandalone = true (the game can run without any AP session).
//   4. NEWS: pull from the GitHub Releases API (title + body + date).
// ════════════════════════════════════════════════════════════════════════════════

public sealed class HighRollerPlugin : IGamePlugin
{
    // ── Constants — GitHub release source ─────────────────────────────────────

    private const string GITHUB_OWNER    = "ElireFeltores";
    private const string GITHUB_REPO     = "High-Roller";
    private const string GH_RELEASES_API =
        $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases";
    private const string RepoUrl         = $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}";

    // Asset names (verified v1.3.0, 2026-06-14).
    private const string GAME_ZIP_NAME   = "High.Roller.zip";
    private const string APWORLD_NAME    = "high_roller.apworld";

    // Pinned fallback (latest verified release as of 2026-06-14).
    private const string FallbackVersion = "1.3.0";
    private static readonly string FallbackGameZipUrl =
        $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}/releases/download/v{FallbackVersion}/{GAME_ZIP_NAME}";

    // Version stamp file — lives in the game folder alongside the extracted game.
    private const string VERSION_STAMP   = "high_roller_ap_version.dat";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "high_roller";
    public string DisplayName => "High Roller";
    public string Subtitle    => "Freeware · Standalone AP client";

    /// EXACT AP game string — verified against world.py:
    ///   class HighRollerWorld(World): game = "High Roller"
    public string ApWorldName => "High Roller";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "high_roller.png");

    public string ThemeAccentColor => "#C0962A";   // gold / slot machine

    public string[] GameBadges => new[] { "Freeware", "ConnectsItself" };

    public string Description =>
        "High Roller is a free, standalone slot-machine game by E-A-V with full " +
        "Archipelago multiworld support built directly into the client. Collect " +
        "buffs to increase your scores on the slots, reach your goal score, and " +
        "send checks to your multiworld. Items, traps, and progression are shuffled " +
        "across your whole multiworld session. The game connects to the Archipelago " +
        "server itself — no emulator, no separate mod needed. Free to play; " +
        "download is handled automatically by the launcher.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }
    public bool    IsInstalled      => FindGameExe() != null;
    public bool    IsRunning        { get; private set; }
    public bool    ConnectsItself   => true;
    public bool    SupportsStandalone => true;

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "Games", "HighRoller");

    private string VersionStampPath =>
        Path.Combine(GameDirectory, VERSION_STAMP);

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The game's built-in AP client reports checks and goal directly to the AP
    // server — the launcher has no bridge role here.
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        // Installed version.
        try
        {
            InstalledVersion = IsInstalled && File.Exists(VersionStampPath)
                ? (await File.ReadAllTextAsync(VersionStampPath, ct)).Trim()
                : null;
        }
        catch { InstalledVersion = null; }

        // Available version from GitHub Releases API.
        try
        {
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(GITHUB_OWNER, GITHUB_REPO, ct));
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((2, "Checking latest High Roller release..."));
        var (version, zipUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        // Fast path: already at this version.
        if (IsInstalled
            && File.Exists(VersionStampPath)
            && (await File.ReadAllTextAsync(VersionStampPath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100, $"High Roller {version} is already up to date."));
            return;
        }

        if (string.IsNullOrWhiteSpace(zipUrl))
            throw new InvalidOperationException(
                "Could not resolve a download URL for High Roller. " +
                "Check your internet connection. Source: " + RepoUrl);

        // Download the game zip.
        string tmpZip = Path.Combine(Path.GetTempPath(),
            $"high-roller-{version}-{Guid.NewGuid():N}.zip");
        string stagingDir = Path.Combine(Path.GetTempPath(),
            $"high-roller-staging-{Guid.NewGuid():N}");
        try
        {
            progress.Report((5, $"Downloading High Roller {version}..."));
            using var resp = await _http.GetAsync(
                zipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            long total      = resp.Content.Headers.ContentLength ?? -1;
            long downloaded = 0;

            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tmpZip))
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
                            $"Downloading High Roller {version}... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            // Extract into a staging directory first.
            progress.Report((72, "Extracting High Roller..."));
            Directory.CreateDirectory(stagingDir);
            ZipFile.ExtractToDirectory(tmpZip, stagingDir, overwriteFiles: true);

            // Merge staging into GameDirectory. The zip may have one top-level
            // folder (e.g. "High Roller\") or drop files directly at the root.
            progress.Report((88, "Installing High Roller..."));
            string mergeRoot = PickMergeRoot(stagingDir);
            Directory.CreateDirectory(GameDirectory);
            MergeDirectory(mergeRoot, GameDirectory);
        }
        finally
        {
            try { if (File.Exists(tmpZip))          File.Delete(tmpZip);                } catch { }
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch { }
        }

        // Stamp version.
        await File.WriteAllTextAsync(VersionStampPath, version,
            new UTF8Encoding(false), ct);
        InstalledVersion = version;

        progress.Report((100,
            $"High Roller {version} is ready. Press Play to launch and connect " +
            "to your Archipelago server from inside the game."));
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
        // High Roller's built-in AP client does not read a pre-written config file;
        // the user enters server details in-game. The settings panel surfaces the
        // session credentials so they can be copied.
        _ = session;
        StartGameProcess();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartGameProcess();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(
        ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var accent  = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0xC0, 0x96, 0x2A));

        var panel = new System.Windows.Controls.StackPanel
            { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Install status ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "INSTALL STATUS",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? exe         = FindGameExe();
        string? installedVer = InstalledVersion;
        try
        {
            installedVer ??= File.Exists(VersionStampPath)
                ? File.ReadAllText(VersionStampPath).Trim()
                : null;
        }
        catch { }

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = exe != null
                ? $"High Roller is installed" +
                  (installedVer != null ? $" (v{installedVer})" : "") +
                  $".\nGame folder: {GameDirectory}"
                : "High Roller is not installed. Click Install in the Play tab to " +
                  "download it automatically (free).",
            FontSize     = 11,
            Foreground   = exe != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Connecting ────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "CONNECTING TO ARCHIPELAGO",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "High Roller has a built-in Archipelago client. After pressing " +
                   "Play, enter your server address, slot name, and password inside " +
                   "the game to connect. Copy your session details from below before " +
                   "launching.",
            FontSize     = 11,
            Foreground   = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── About ─────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "ABOUT",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "High Roller is a free, standalone slot-machine-themed game by " +
                   "E-A-V with native Archipelago multiworld support. Collect buffs " +
                   "to raise your slot scores and reach your goal. No Steam required. " +
                   "AP World minimum version: 0.6.4.",
            FontSize     = 11,
            Foreground   = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "LINKS",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 0, 0, 8),
        });

        foreach (var (label, url) in new[]
        {
            ("High Roller (GitHub) ↗",      RepoUrl),
            ("High Roller Releases ↗",       $"{RepoUrl}/releases"),
            ("Archipelago Official ↗",        "https://archipelago.gg"),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content             = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding             = new System.Windows.Thickness(0, 2, 0, 2),
                Background          = System.Windows.Media.Brushes.Transparent,
                BorderThickness     = new System.Windows.Thickness(0),
                FontSize            = 12,
                Margin              = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground          = new System.Windows.Media.SolidColorBrush(
                                          System.Windows.Media.Color.FromRgb(0xC0, 0x96, 0x2A)),
                Cursor              = System.Windows.Input.Cursors.Hand,
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
            using var doc  = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d)
                    && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : "",
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: el.TryGetProperty("tag_name", out var t)
                                 ? NormalizeTag(t.GetString()) ?? "" : "",
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
            ? tag[1..] : tag;
    }

    /// Resolve the latest GitHub release that ships the "High.Roller.zip" game
    /// asset. Returns (version, gameZipUrl). Falls back to the pinned v1.3.0
    /// values when the API is unreachable.
    private async Task<(string Version, string? ZipUrl)>
        ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_API, ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind != JsonValueKind.Array
                || doc.RootElement.GetArrayLength() == 0)
                return (FallbackVersion, FallbackGameZipUrl);

            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                // Skip drafts.
                if (rel.TryGetProperty("draft", out var dr)
                    && dr.ValueKind == JsonValueKind.True)
                    continue;

                string? version = rel.TryGetProperty("tag_name", out var t)
                    ? NormalizeTag(t.GetString()) : null;
                if (version == null) continue;

                if (!rel.TryGetProperty("assets", out var assets)
                    || assets.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var asset in assets.EnumerateArray())
                {
                    string? name = asset.TryGetProperty("name", out var n)
                        ? n.GetString() : null;
                    string? url  = asset.TryGetProperty("browser_download_url", out var u)
                        ? u.GetString() : null;

                    if (name == null || url == null) continue;

                    // Match the game zip (not the apworld).
                    if (name.Equals(GAME_ZIP_NAME, StringComparison.OrdinalIgnoreCase))
                        return (version, url);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* network failure → pinned fallback */ }

        return (FallbackVersion, FallbackGameZipUrl);
    }

    // ── Private helpers — game directory ──────────────────────────────────────

    /// Find the game executable in GameDirectory. The zip may extract into a
    /// sub-folder; we scan one level deep for any .exe file.
    private string? FindGameExe()
    {
        try
        {
            if (!Directory.Exists(GameDirectory)) return null;

            // Check directly in the game directory first.
            string? direct = Directory.EnumerateFiles(
                GameDirectory, "*.exe", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f =>
                {
                    string name = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    // Prefer names containing "high" and/or "roller".
                    return name.Contains("high") || name.Contains("roller");
                });
            if (direct != null) return direct;

            // Fall back to any .exe in the top-level game directory.
            return Directory.EnumerateFiles(
                GameDirectory, "*.exe", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    /// Pick the folder inside the staging directory whose CONTENTS should be
    /// merged into GameDirectory. If the zip dropped everything into a single
    /// sub-folder (e.g. "High Roller\"), return that sub-folder so the merge
    /// lands one level up. Otherwise return the staging root.
    private static string PickMergeRoot(string stagingDir)
    {
        try
        {
            var dirs  = Directory.GetDirectories(stagingDir, "*", SearchOption.TopDirectoryOnly);
            var files = Directory.GetFiles(stagingDir, "*", SearchOption.TopDirectoryOnly);

            // If the zip extracted into exactly one sub-folder and no loose files,
            // descend into that folder.
            if (dirs.Length == 1 && files.Length == 0)
                return dirs[0];
        }
        catch { }
        return stagingDir;
    }

    /// Recursively merge the contents of src into dst (create directories,
    /// overwrite files).
    private static void MergeDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string fileSrc in Directory.EnumerateFiles(
            src, "*", SearchOption.AllDirectories))
        {
            string rel     = Path.GetRelativePath(src, fileSrc);
            string fileDst = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(fileDst)!);
            File.Copy(fileSrc, fileDst, overwrite: true);
        }
    }

    // ── Private helpers — process ─────────────────────────────────────────────

    private void StartGameProcess()
    {
        string? exe = FindGameExe();
        if (exe == null)
            throw new FileNotFoundException(
                "Could not find the High Roller executable. " +
                "Click Install Game to download it first.",
                Path.Combine(GameDirectory, "*.exe"));

        string workDir = Path.GetDirectoryName(exe) ?? GameDirectory;
        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = exe,
            WorkingDirectory = workDir,
            UseShellExecute  = true,
        }) ?? throw new InvalidOperationException("Failed to start High Roller.");

        _gameProcess = proc;
        IsRunning    = true;

        try
        {
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) =>
            {
                IsRunning    = false;
                _gameProcess = null;
                GameExited?.Invoke(proc.ExitCode);
            };
        }
        catch { /* some processes don't raise Exited — IsRunning stays true until StopAsync */ }
    }
}
