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
using LauncherV2.Core;

// NOTE: WPF UI types are FULLY QUALIFIED throughout this file (System.Windows.*).
// The real launcher project sets <UseWindowsForms>true</UseWindowsForms> alongside
// WPF, so the bare names Button / TextBox / Color / Brushes / MessageBox /
// FontWeights / Orientation / Clipboard collide between System.Windows.Forms and
// System.Windows[.Controls/.Media] (CS0104). Qualifying every UI type avoids that.
// GlobalUsings.cs already aliases the colliding short names project-wide — so this
// file must NOT add any file-level `using X = System.Windows...;` alias (CS1537).

namespace LauncherV2.Plugins.SeveredSoul;

// ═══════════════════════════════════════════════════════════════════════════════
// SeveredSoulPlugin — install / update / launch for "Severed Soul" by Grenhunterr,
// an indie action game distributed as an Archipelago mod from GitHub. This is a
// NATIVE "ConnectsItself" integration: the game ships its own Archipelago client,
// so the launcher must NOT hold its own ApClient on the same slot while the game
// runs (AP servers allow only one connection per slot).
//
// DISTRIBUTION MODEL
// ──────────────────
// Severed Soul is NOT on Steam. The Archipelago-enabled build is distributed via
// GitHub releases at: https://github.com/Grenhunterr/Archipelago
// The mod IS the game (fully self-contained download), so install = download the
// release zip and extract it into GameDirectory. No bring-your-own-asset gate.
//
// CONNECTION INTERFACE
// ────────────────────
// Because there is no publicly documented setup guide at time of writing, the
// connection mechanism is inferred from the standard pattern used by Grenhunterr's
// AP fork (Archipelago-fork direct release). The launcher writes an "ap_config.json"
// file into the game directory before launch with the standard AP connection fields.
// If the game uses command-line arguments instead, the LaunchAsync method also
// passes them as a fallback. The player can always edit the config file manually;
// the launcher uses read-modify-write to preserve any keys it doesn't know about.
//
// AP WORLD
// ────────
// The AP world game string is "Severed Soul" — sourced from Grenhunterr/Archipelago.
// ConnectsItself = true: the game's built-in AP client owns the slot connection.
//
// INSTALLATION
// ────────────
// Install is a single download + extract into Games/SeveredSoul. The exe is located
// by scanning for any non-setup/non-crash *.exe in the GameDirectory. A version
// stamp file ("severed_soul_version.dat") is written after a successful install.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SeveredSoulPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GITHUB_OWNER        = "Grenhunterr";
    private const string GITHUB_REPO         = "Archipelago";
    private const string RepoUrl             = $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}";
    private const string GH_RELEASES_URL     =
        $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases";
    private const string GH_RELEASES_LATEST  = $"{GH_RELEASES_URL}/latest";

    private const string VersionFileName     = "severed_soul_version.dat";
    private const string ApConfigFileName    = "ap_config.json";
    private const int    DefaultApPort       = 38281;

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId        => "severed_soul";
    public string DisplayName   => "Severed Soul";
    public string Subtitle      => "Native PC · built-in Archipelago";
    public string ApWorldName   => "Severed Soul";
    public string ThemeAccentColor => "#8B1A1A";   // dark crimson / soul aesthetic
    public string[] GameBadges  => new[] { "Indie · ConnectsItself" };

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "severed_soul.png");

    public string Description =>
        "Severed Soul is an indie action game by Grenhunterr with a built-in " +
        "Archipelago Multiworld client. Items, abilities, and progression are " +
        "shuffled into the multiworld — the game connects to the Archipelago server " +
        "itself, so no emulator, no Lua bridge, and no extra client are needed. " +
        "Install is a single download from GitHub: the mod is the game, and there " +
        "is nothing else to bring. The launcher fills in your connection details and " +
        "launches you straight in.";

    public string? VideoPreviewUrl  => null;
    public string[] ScreenshotUrls  => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion  { get; private set; }
    public string? AvailableVersion  { get; private set; }
    public bool    IsInstalled       => ResolveGameExe() != null;
    public bool    IsRunning         { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "Games", "SeveredSoul");

    private string VersionFilePath => Path.Combine(GameDirectory, VersionFileName);
    private string ApConfigPath    => Path.Combine(GameDirectory, ApConfigFileName);

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge — inert (ConnectsItself = true) ────────────────────────────

#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── IGamePlugin — Capabilities ────────────────────────────────────────────

    public bool ConnectsItself     => true;
    public bool SupportsStandalone => true;
    public string? BuiltAgainstDataPackageChecksum => null;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        // Reload the installed version from disk (may have been installed externally).
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

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((2, "Checking latest Severed Soul release..."));
        var (version, zipUrl, apworldUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        // Idempotent fast path — already up to date.
        if (IsInstalled
            && File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100, $"Severed Soul {version} is up to date."));
            return;
        }

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find a Windows download for Severed Soul on the GitHub " +
                "release page. Check your internet connection, or download the build " +
                "manually from " + RepoUrl + "/releases.");

        // Download and extract the build.
        await DownloadAndExtractAsync(zipUrl, version ?? "unknown", progress, ct);

        // Opportunistically fetch the apworld if the release ships one.
        if (apworldUrl != null)
        {
            try
            {
                progress.Report((85, "Downloading Severed Soul apworld..."));
                byte[] apworld = await _http.GetByteArrayAsync(apworldUrl, ct);
                string apworldPath = Path.Combine(GameDirectory, "severed_soul.apworld");
                await File.WriteAllBytesAsync(apworldPath, apworld, ct);
                progress.Report((92,
                    "severed_soul.apworld saved — copy it into Archipelago's " +
                    "custom_worlds folder if you need to generate a multiworld."));
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                progress.Report((92,
                    "Could not download the apworld — you can grab it from the " +
                    "GitHub releases page if needed."));
            }
        }

        // Stamp the installed version.
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        progress.Report((100,
            $"Severed Soul {version} ready. Press Play to connect — " +
            "the launcher fills in your AP connection details automatically."));
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
                "Severed Soul is not installed. Click Install Game first.",
                Path.Combine(GameDirectory, "severed_soul.exe"));

        // Write the AP connection config file before launch so the game can pick
        // it up. Read-modify-write preserves any keys the game writes itself.
        try { WriteApConfig(session); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }

        // Also build command-line args as a fallback in case the game uses them.
        var (host, port) = ParseServerHostPort(session.ServerUri);
        string[] args = BuildLaunchArgs(host, port, session.SlotName, session.Password);

        StartGameProcess(exe, args);
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        string exe = ResolveGameExe()
            ?? throw new FileNotFoundException(
                "Severed Soul is not installed. Click Install Game first.",
                Path.Combine(GameDirectory, "severed_soul.exe"));

        // Disable AP in the config file so a previous session doesn't auto-connect.
        try { DisableApConfig(); } catch { }

        StartGameProcess(exe, Array.Empty<string>());
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        ScrubApConfigPassword();
        return Task.CompletedTask;
    }

    // ── AP bridge — inert ─────────────────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // Severed Soul's native AP client receives items from the server directly.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The game renders its own AP status in-game; no launcher HUD channel.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var crimson = System.Windows.Media.Color.FromRgb(0x8B, 0x1A, 0x1A);
        var accent  = new System.Windows.Media.SolidColorBrush(crimson);
        var muted   = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x7A, 0x62, 0x62));
        var fg      = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xCC, 0xC0, 0xC0));
        var success = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));

        var panel = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20)
        };

        // ── Section header helper ─────────────────────────────────────────
        System.Windows.Controls.TextBlock SectionHeader(string text) =>
            new()
            {
                Text       = text,
                FontSize   = 10,
                FontWeight = System.Windows.FontWeights.SemiBold,
                Foreground = muted,
                Margin     = new System.Windows.Thickness(0, 0, 0, 8),
            };

        // ── Section: Install directory ────────────────────────────────────
        panel.Children.Add(SectionHeader("INSTALL DIRECTORY"));

        var dirRow = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4)
        };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text        = GameDirectory,
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x2A, 0x1A, 0x1A)),
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content     = "Browse...",
            Width       = 90,
            Padding     = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x22, 0x10, 0x10)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x50, 0x22, 0x22)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select Severed Soul install folder",
                InitialDirectory = Directory.Exists(GameDirectory) ? GameDirectory
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                GameDirectory = dlg.FolderName;
                dirBox.Text   = dlg.FolderName;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(dirBtn, System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = IsInstalled
                ? "✓ Severed Soul is installed"
                : "Not installed — click Install in the Play tab",
            FontSize   = 11,
            Foreground = IsInstalled ? success : muted,
            Margin     = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: How it connects ──────────────────────────────────────
        panel.Children.Add(SectionHeader("ARCHIPELAGO CONNECTION"));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "Severed Soul ships its own built-in Archipelago client. The launcher " +
                "writes an ap_config.json file into the install folder (server, port, " +
                "slot_name, password) each time you press Play, then launches the game. " +
                "You can edit ap_config.json by hand — the launcher preserves any extra " +
                "keys. Because the game connects itself, the launcher does not hold an " +
                "AP session on your slot while the game is running.",
            FontSize     = 11,
            Foreground   = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 12),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "The download is the complete game — there is nothing to bring. " +
                "Install is just a download from GitHub.",
            FontSize     = 11,
            Foreground   = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Links ────────────────────────────────────────────────
        panel.Children.Add(SectionHeader("LINKS"));

        foreach (var (label, url) in new[]
        {
            ("Severed Soul / Grenhunterr Archipelago (GitHub) ↗", RepoUrl),
            ("Archipelago Official ↗",                            "https://archipelago.gg"),
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
                    System.Windows.Media.Color.FromRgb(0xC0, 0x55, 0x55)),
                Cursor              = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(u) { UseShellExecute = true });
                }
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
            string json = await _http.GetStringAsync(GH_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);
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
                    Version: el.TryGetProperty("tag_name", out var t) ? NormalizeTag(t.GetString()) ?? "" : "",
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var u) ? u.GetString() : null
                ));

                if (items.Count >= 20) break;
            }

            return items.ToArray();
        }
        catch
        {
            return Array.Empty<NewsItem>();
        }
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    /// "v1.0.0" → "1.0.0". Returns the raw tag for non-standard formats. Null for null/blank.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..] : tag;
    }

    /// Resolve the latest release from GitHub. Returns (version, zipUrl, apworldUrl).
    /// Falls back to returning (null, null, null) when the API is unreachable.
    private async Task<(string? Version, string? ZipUrl, string? ApWorldUrl)>
        ResolveLatestReleaseAsync(CancellationToken ct)
    {
        // Try /releases/latest first (fastest, non-prerelease).
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_LATEST, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString()) : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                && assets.ValueKind == JsonValueKind.Array)
            {
                var (zip, apworld) = PickWindowsAndApworld(assets);
                if (zip != null) return (version, zip, apworld);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* fall through to paginated releases list */ }

        // Fall back to /releases (handles prerelease-only repos).
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                string? version = el.TryGetProperty("tag_name", out var t)
                    ? NormalizeTag(t.GetString()) : null;
                if (version == null) continue;

                if (el.TryGetProperty("assets", out var assets)
                    && assets.ValueKind == JsonValueKind.Array)
                {
                    var (zip, apworld) = PickWindowsAndApworld(assets);
                    if (zip != null) return (version, zip, apworld);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        return (null, null, null);
    }

    /// From a release's assets array, pick the best Windows zip and any apworld asset.
    private static (string? Zip, string? ApWorld) PickWindowsAndApworld(JsonElement assets)
    {
        string? win64 = null, win32 = null, anyWinZip = null, anyZip = null;
        string? apworld = null;

        foreach (var a in assets.EnumerateArray())
        {
            string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name == null || url == null) continue;

            string lower = name.ToLowerInvariant();

            if (lower.EndsWith(".apworld"))
            {
                apworld ??= url;
            }
            else if (lower.EndsWith(".zip")
                && !lower.Contains("source")
                && !lower.Contains("linux")
                && !lower.Contains("ubuntu")
                && !lower.Contains("mac")
                && !lower.Contains("osx")
                && !lower.Contains("darwin"))
            {
                anyZip ??= url;
                if (lower.Contains("win64") || lower.Contains("win-x64") || lower.Contains("x86_64"))
                    win64 ??= url;
                else if (lower.Contains("win32") || lower.Contains("win-x86"))
                    win32 ??= url;
                else if (lower.Contains("win") || lower.Contains("x64"))
                    anyWinZip ??= url;
            }
        }

        string? zip = win64 ?? win32 ?? anyWinZip ?? anyZip;
        return (zip, apworld);
    }

    // ── Private helpers — exe resolution ──────────────────────────────────────

    /// Find the game exe: prefer "severed_soul.exe" or "SeveredSoul.exe", then any
    /// exe in the install that is not a helper / uninstaller / crash reporter.
    private string? ResolveGameExe()
    {
        if (!Directory.Exists(GameDirectory)) return null;

        // Well-known names (best guess — verify when a real release is inspected).
        foreach (string candidate in new[]
        {
            "severed_soul.exe",
            "SeveredSoul.exe",
            "severedsoul.exe",
            "SeveredSoul.x86_64",
        })
        {
            string path = Path.Combine(GameDirectory, candidate);
            if (File.Exists(path)) return path;
        }

        // Fuzzy: any exe in the install tree that isn't an obvious helper.
        try
        {
            foreach (string exe in Directory.EnumerateFiles(
                GameDirectory, "*.exe", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (name.Contains("unins") || name.Contains("setup")
                    || name.Contains("crash") || name.Contains("redist"))
                    continue;
                return exe;
            }
        }
        catch { /* directory vanished mid-scan */ }

        return null;
    }

    // ── Private helpers — AP config file ─────────────────────────────────────

    /// Write the AP connection config file before launch.
    /// Uses read-modify-write to preserve any keys the game writes itself.
    private void WriteApConfig(ApSession session)
    {
        Directory.CreateDirectory(GameDirectory);
        var (host, port) = ParseServerHostPort(session.ServerUri);

        MutateApConfig(root =>
        {
            root["server"]    = host;
            root["port"]      = (long)port;
            root["slot_name"] = session.SlotName;
            root["password"]  = string.IsNullOrEmpty(session.Password)
                                    ? (object?)null
                                    : session.Password;
            root["ap_enable"] = true;
        }, createIfMissing: true);
    }

    /// Write ap_enable=false before a standalone (non-AP) launch so the game does
    /// not silently reconnect to a previous slot.
    private void DisableApConfig()
    {
        if (!File.Exists(ApConfigPath)) return;
        MutateApConfig(root =>
        {
            root["ap_enable"] = false;
            if (root.ContainsKey("password")) root["password"] = null;
        });
    }

    /// Blank the password field once the session ends.
    private void ScrubApConfigPassword()
    {
        try
        {
            if (!File.Exists(ApConfigPath)) return;
            MutateApConfig(root =>
            {
                if (root.TryGetValue("password", out var pw)
                    && pw is string s && !string.IsNullOrEmpty(s))
                    root["password"] = null;
            });
        }
        catch { /* best effort */ }
    }

    /// Read ap_config.json → mutate → write back (BOM-less UTF-8).
    /// Preserves every key the mutator does not touch.
    private void MutateApConfig(Action<Dictionary<string, object?>> mutate,
                                bool createIfMissing = false)
    {
        var root     = new Dictionary<string, object?>();
        bool haveFile = false;

        try
        {
            if (File.Exists(ApConfigPath))
            {
                string text = File.ReadAllText(ApConfigPath);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text);
                    if (parsed != null)
                        foreach (var kv in parsed)
                            root[kv.Key] = JsonElementToObject(kv.Value);
                }
                haveFile = true;
            }
        }
        catch
        {
            root.Clear(); // corrupt — start fresh
        }

        if (!haveFile && !createIfMissing) return;

        mutate(root);

        File.WriteAllText(ApConfigPath,
            JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    /// Convert a JsonElement to a plain object for round-trip serialisation.
    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out long l) ? l : el.GetDouble(),
        JsonValueKind.True   => true,
        JsonValueKind.False  => false,
        JsonValueKind.Null   => null,
        _                    => el.Clone(),
    };

    // ── Private helpers — command-line args ───────────────────────────────────

    /// Build launch args for games that accept command-line AP credentials as a
    /// fallback alongside the config file (common AP fork pattern).
    private static string[] BuildLaunchArgs(
        string host, int port, string slotName, string password)
    {
        var args = new List<string>
        {
            "--archipelago-host",     host,
            "--archipelago-port",     port.ToString(),
            "--archipelago-slot",     slotName,
        };
        if (!string.IsNullOrEmpty(password))
        {
            args.Add("--archipelago-password");
            args.Add(password);
        }
        return args.ToArray();
    }

    // ── Private helpers — server URI parsing ──────────────────────────────────

    /// Parse "archipelago.gg:38281", "ws://host:port", bare host, IPv6 brackets.
    /// Returns (host, port). Default AP port is 38281.
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
        int    port = DefaultApPort;

        int colonCount = 0;
        foreach (char c in s) if (c == ':') colonCount++;

        if (s.StartsWith('['))
        {
            int close = s.IndexOf(']');
            if (close > 0)
            {
                host = s[1..close];
                string rest = s[(close + 1)..];
                if (rest.StartsWith(':')
                    && int.TryParse(rest[1..], out int p6)
                    && p6 > 0 && p6 <= 65535)
                    port = p6;
            }
        }
        else if (colonCount > 1)
        {
            host = s; // bare IPv6 — no port
        }
        else
        {
            int colon = s.LastIndexOf(':');
            if (colon > 0
                && int.TryParse(s[(colon + 1)..], out int p)
                && p > 0 && p <= 65535)
            {
                host = s[..colon];
                port = p;
            }
        }

        if (host.Length == 0) host = "archipelago.gg";
        return (host, port);
    }

    // ── Private helpers — process ─────────────────────────────────────────────

    private void StartGameProcess(string exePath, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName         = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? GameDirectory,
            UseShellExecute  = false,
        };
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start Severed Soul.");

        _gameProcess = proc;
        IsRunning    = true;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            IsRunning = false;
            ScrubApConfigPassword();
            GameExited?.Invoke(proc.ExitCode);
        };
    }

    // ── Private helpers — download / extract ─────────────────────────────────

    private async Task DownloadAndExtractAsync(
        string zipUrl,
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"severed_soul-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((5, $"Downloading Severed Soul {version}..."));
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
                        progress.Report((pct,
                            $"Downloading Severed Soul... {downloaded / 1_000_000}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((70, "Extracting..."));
            Directory.CreateDirectory(GameDirectory);
            ZipFile.ExtractToDirectory(tempZip, GameDirectory, overwriteFiles: true);

            // Flatten single top-level subfolder so the exe lands at the install root.
            if (Directory.GetFiles(GameDirectory).Length == 0)
            {
                string[] subdirs = Directory.GetDirectories(GameDirectory);
                if (subdirs.Length == 1)
                {
                    string sub = subdirs[0];
                    foreach (string fileSrc in
                        Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories))
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
