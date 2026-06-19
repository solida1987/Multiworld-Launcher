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

namespace LauncherV2.Plugins.Balatro;

// ═══════════════════════════════════════════════════════════════════════════════
// BalatroPlugin — install / update / launch for Balatro + BalatroAP mod.
//
// ── VERIFIED FACTS (2026-06-14, sources: BurndiL/BalatroAP repo + README) ─────
//
//   * GAME: Balatro (Steam appid 2379780, LÖVE2D engine, Lua mods).
//
//   * AP INTEGRATION: BurndiL/BalatroAP — a Lua mod for Balatro. Latest release
//     v0.1.9f. Assets per release: balatro.apworld, Balatro.yaml, BalatroAP.zip.
//     AP game string (from ap_connection.lua: AP(uuid, "Balatro", server)):
//       → "Balatro"
//
//   * MOD LOADER: Steamodded (github.com/Steamodded/smods).
//     Steamodded itself requires "Lovely Injector" (github.com/nicholasgasior/lovely):
//       - Download lovely-x86_64-pc-windows-msvc.zip from Lovely releases.
//       - Extract version.dll into Balatro's Steam game directory.
//     Mods (including Steamodded and BalatroAP) live under:
//       %AppData%\Balatro\Mods\
//     BalatroAP mod specifically: %AppData%\Balatro\Mods\BalatroAP\
//     (BalatroAP.zip already contains a top-level "BalatroAP" folder.)
//
//   * CONNECTION CONFIG: APSettings.json written by the mod at:
//       %AppData%\Balatro\APSettings.json
//     Format (verified from randomizer.lua json.encode block):
//       { "APAddress": "host", "APPort": "38281", "APSlot": "slot", "APPassword": "pw" }
//     Note: APPort is a STRING in the JSON (the Lua field G.AP.APPort is a string).
//     The mod reads this file when the Archipelago profile is loaded (profile select).
//     This plugin pre-writes the file so the connection fields are pre-filled.
//
//   * HOW IT CONNECTS (verified from README):
//     Select the "Archipelago" profile in the profile selector (bottom-left of main
//     menu). The connection info from APSettings.json is pre-loaded, so the user
//     just clicks Connect. ConnectsItself = true (the Lua mod owns the AP slot —
//     the launcher must not hold its own ApClient on the same slot).
//     SupportsStandalone = true (Balatro plays fine without AP profile selected).
//
//   * LAUNCH: Steam protocol "steam://rungameid/2379780" (Steam-only game per the
//     mod's README: "This mod only works with the Steam version of Balatro").
//     The launcher also tries to locate the exe from the Steam registry for direct
//     launch if the user prefers it.
//
//   * BASE GAME: user-owned on Steam. The launcher DETECTS the install; never
//     downloads or owns the game exe.
//
// ── WHAT THIS PLUGIN DOES ─────────────────────────────────────────────────────
//   1. Detect Balatro's Steam install via registry + appmanifest_2379780.acf.
//      Manual override persisted in Games/ROMs/balatro/balatro_launcher.json.
//   2. Check Steamodded (lovely's version.dll in the game dir). If missing:
//      guide user with download link + step-by-step instructions.
//   3. Download and install BalatroAP mod from BurndiL/BalatroAP releases:
//      - Download BalatroAP.zip
//      - Extract into %AppData%\Balatro\Mods\  (top-level "BalatroAP" sub-folder)
//   4. Pre-write APSettings.json with the session's server/slot/password.
//   5. Launch via Steam or direct exe.
//   6. Settings panel: game path, Steamodded status, mod status, links.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class BalatroPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string MOD_OWNER       = "BurndiL";
    private const string MOD_REPO        = "BalatroAP";
    private const string ModRepoUrl      = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";
    private const string GH_RELEASES_LATEST_URL = $"{GH_RELEASES_URL}/latest";

    // Lovely Injector (provides version.dll which loads Steamodded).
    private const string LovelyReleasesUrl =
        "https://github.com/nicholasgasior/lovely/releases/latest";

    // Steamodded install guide.
    private const string SteamoddedWikiUrl =
        "https://github.com/Steamodded/smods/wiki";

    private const int STEAM_APPID = 2379780;

    /// Pinned fallback version if GitHub API is unreachable.
    private const string FallbackVersion = "v0.1.9f";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(15),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── Per-game sidecar (persists game dir override) ─────────────────────────

    private sealed class BalatroSidecar
    {
        public string? GameDirectory { get; set; }
    }

    private string SidecarPath
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", "balatro", "balatro_launcher.json");

    private BalatroSidecar LoadSidecar()
    {
        try
        {
            if (File.Exists(SidecarPath))
            {
                string txt = File.ReadAllText(SidecarPath);
                return JsonSerializer.Deserialize<BalatroSidecar>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSidecar(BalatroSidecar s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SidecarPath)!);
            File.WriteAllText(SidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { }
    }

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "balatro";
    public string DisplayName => "Balatro";
    public string Subtitle    => "Roguelike card game · Lua mod · built-in AP client";
    public string ApWorldName => "Balatro";

    public string IconPath
        => Path.Combine(AppContext.BaseDirectory, "Assets", "balatro.png");

    public string ThemeAccentColor => "#B22222";   // deep poker red

    public string[] GameBadges => new[] { "Requires Steam" };

    public string Description =>
        "Balatro is a poker-themed roguelike card game where you play illegal " +
        "poker hands, discover joker cards, and create powerful synergies to beat " +
        "increasingly difficult antes. The BalatroAP mod integrates Archipelago " +
        "multiworld: every joker, voucher, booster pack, and consumable starts " +
        "locked and must be unlocked by receiving them as Archipelago items. " +
        "Checks are earned by beating boss blinds across different decks and stakes. " +
        "Requires a Steam copy of Balatro and the Steamodded mod loader.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    public bool IsInstalled
    {
        get
        {
            string modsDir = BalatroModsAppDataDir;
            if (string.IsNullOrEmpty(modsDir)) return false;
            string modFolder = Path.Combine(modsDir, "BalatroAP");
            // Consider installed if the mod folder exists and has at least one Lua file.
            if (!Directory.Exists(modFolder)) return false;
            try
            {
                foreach (string _ in Directory.EnumerateFiles(modFolder, "*.lua",
                             SearchOption.TopDirectoryOnly))
                    return true;
            }
            catch { }
            return false;
        }
    }

    public bool IsRunning { get; private set; }

    public bool ConnectsItself    => true;
    public bool SupportsStandalone => true;

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Game install directory — detected from Steam or overridden by user.
    public string GameDirectory
    {
        get
        {
            var sidecar = LoadSidecar();
            if (!string.IsNullOrEmpty(sidecar.GameDirectory) &&
                Directory.Exists(sidecar.GameDirectory))
                return sidecar.GameDirectory;
            return DetectSteamGameDir() ?? "";
        }
        set
        {
            var sidecar = LoadSidecar();
            sidecar.GameDirectory = value;
            SaveSidecar(sidecar);
        }
    }

    /// %AppData%\Balatro\Mods\ — where all Balatro mods live (per Steamodded spec).
    private static string BalatroModsAppDataDir
    {
        get
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "Balatro", "Mods");
        }
    }

    /// %AppData%\Balatro\APSettings.json — connection config the mod reads.
    private static string ApSettingsPath
    {
        get
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "Balatro", "APSettings.json");
        }
    }

    private string VersionStampPath
        => Path.Combine(BalatroModsAppDataDir, "BalatroAP", ".launcher_version.dat");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The BalatroAP Lua mod connects directly to the AP server; the launcher
    // only tracks process lifetime and pre-writes connection credentials.
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067
    public event Action<int>? GameExited;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        // Read installed version from our stamp file.
        try
        {
            InstalledVersion = IsInstalled && File.Exists(VersionStampPath)
                ? (await File.ReadAllTextAsync(VersionStampPath, ct)).Trim()
                : null;
        }
        catch { InstalledVersion = null; }

        // Query GitHub for latest release tag.
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            AvailableVersion = doc.RootElement.TryGetProperty("tag_name", out var t)
                ? t.GetString()?.Trim()
                : null;
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((2, "Checking latest BalatroAP release..."));

        var (version, zipUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        // Fast path: already up to date.
        if (IsInstalled && File.Exists(VersionStampPath))
        {
            string stamped = (await File.ReadAllTextAsync(VersionStampPath, ct)).Trim();
            if (stamped == version)
            {
                InstalledVersion = version;
                progress.Report((100, $"BalatroAP {version} is already installed."));
                return;
            }
        }

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find BalatroAP.zip on the GitHub release page. " +
                "Check your internet connection, or install manually from " + ModRepoUrl + "/releases.");

        // Ensure Mods directory exists.
        string modsDir = BalatroModsAppDataDir;
        Directory.CreateDirectory(modsDir);

        // Download BalatroAP.zip.
        string tempZip = Path.Combine(Path.GetTempPath(), $"balatroap-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((5, $"Downloading BalatroAP {version}..."));
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
                        int pct = (int)(5 + 55 * downloaded / total);
                        progress.Report((pct, $"Downloading BalatroAP... {downloaded / 1024}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((65, "Extracting BalatroAP mod files..."));

            // Remove old install before extracting so stale files don't linger.
            string modFolder = Path.Combine(modsDir, "BalatroAP");
            if (Directory.Exists(modFolder))
            {
                try { Directory.Delete(modFolder, recursive: true); } catch { }
            }

            // BalatroAP.zip contains a top-level "BalatroAP" folder — extract
            // directly into modsDir so it lands at modsDir\BalatroAP\.
            ZipFile.ExtractToDirectory(tempZip, modsDir, overwriteFiles: true);

            // Safety: if the zip happened to land without the sub-folder name,
            // flatten everything into modsDir\BalatroAP\.
            if (!Directory.Exists(modFolder))
            {
                Directory.CreateDirectory(modFolder);
                foreach (string f in Directory.GetFiles(modsDir, "*", SearchOption.TopDirectoryOnly))
                {
                    string fname = Path.GetFileName(f);
                    if (fname.StartsWith("balatroap-", StringComparison.OrdinalIgnoreCase)
                        || fname.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        continue;
                    File.Move(f, Path.Combine(modFolder, fname), overwrite: true);
                }
            }

            progress.Report((85, "BalatroAP mod files extracted."));

            // Stamp installed version.
            await File.WriteAllTextAsync(VersionStampPath, version, ct);
            InstalledVersion = version;

            progress.Report((100,
                $"BalatroAP {version} installed. " +
                "Make sure Steamodded (version.dll + smods) is also installed — see the Settings tab."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        if (!IsInstalled) return false;
        // Basic: check that the core Lua files exist.
        string modFolder = Path.Combine(BalatroModsAppDataDir, "BalatroAP");
        return File.Exists(Path.Combine(modFolder, "ap_connection.lua"))
            || File.Exists(Path.Combine(modFolder, "randomizer.lua"));
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // Pre-write APSettings.json so the mod picks up the connection details
        // when the user selects the Archipelago profile.
        try { WriteApSettings(session); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        StartGame();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartGame();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        ScrubApSettingsPassword();
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (mod speaks directly to AP server) ─────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted    = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg       = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var ok       = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn     = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xA5, 0x00));
        var linkClr  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF));

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Section: Game install directory ───────────────────────────────
        panel.Children.Add(SectionHeader("BALATRO GAME DIRECTORY", muted));

        string gameDir  = GameDirectory;
        bool   gameFound = !string.IsNullOrEmpty(gameDir) && Directory.Exists(gameDir);

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = gameDir, IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var browseBtn = MakeButton("Browse...", fg, 90);
        browseBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select Balatro game folder (contains Balatro.exe)",
                InitialDirectory = gameFound ? gameDir : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                GameDirectory = dlg.FolderName;
                dirBox.Text   = dlg.FolderName;
            }
        };
        DockPanel.SetDock(browseBtn, Dock.Right);
        dirRow.Children.Add(browseBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = gameFound
                ? "Balatro game folder detected."
                : "Balatro not found. Browse to the Steam install, or install via Steam first.",
            FontSize = 11,
            Foreground = gameFound ? ok : warn,
            Margin = new Thickness(0, 4, 0, 14),
        });

        // ── Section: Steamodded status ────────────────────────────────────
        panel.Children.Add(SectionHeader("STEAMODDED (MOD LOADER)", muted));

        bool steamoddedInstalled = IsSteamoddedInstalled();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = steamoddedInstalled
                ? "Steamodded is installed (version.dll found in game folder)."
                : "Steamodded is NOT installed. BalatroAP will not load without it.",
            FontSize = 11,
            Foreground = steamoddedInstalled ? ok : warn,
            Margin = new Thickness(0, 0, 0, 6),
        });

        if (!steamoddedInstalled)
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "To install Steamodded:\n" +
                       "1. Download Lovely Injector from GitHub (see link below).\n" +
                       "2. Extract version.dll into your Balatro game folder.\n" +
                       "3. Download the latest Steamodded (smods) release from GitHub.\n" +
                       "4. Extract the smods folder into %AppData%\\Balatro\\Mods\\",
                FontSize = 11, Foreground = muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
            });
        }

        // ── Section: BalatroAP mod status ─────────────────────────────────
        panel.Children.Add(SectionHeader("BALATROAP MOD", muted));

        bool modInstalled = IsInstalled;
        string modDir     = Path.Combine(BalatroModsAppDataDir, "BalatroAP");
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modInstalled
                ? $"BalatroAP is installed at:\n{modDir}"
                : $"BalatroAP is NOT installed.\nExpected location: {modDir}",
            FontSize = 11,
            Foreground = modInstalled ? ok : warn,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        });

        if (!string.IsNullOrEmpty(InstalledVersion))
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = $"Installed version: {InstalledVersion}" +
                       (AvailableVersion != null && AvailableVersion != InstalledVersion
                           ? $"  (update available: {AvailableVersion})"
                           : ""),
                FontSize = 11, Foreground = muted,
                Margin = new Thickness(0, 0, 0, 6),
            });
        }

        // ── Section: How to connect ───────────────────────────────────────
        panel.Children.Add(SectionHeader("HOW TO CONNECT", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "1. Generate a multiworld seed with the balatro.apworld file.\n" +
                   "2. Launch the game via the Play tab (connection info is pre-filled).\n" +
                   "3. In the Balatro main menu, select the \"Archipelago\" profile " +
                   "(bottom-left profile selector).\n" +
                   "4. Your server/slot/password will already be set — click Connect.",
            FontSize = 11, Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: Links ────────────────────────────────────────────────
        panel.Children.Add(SectionHeader("LINKS", muted));

        foreach (var (label, url) in new[]
        {
            ("BalatroAP mod (GitHub) ↗",           ModRepoUrl),
            ("BalatroAP releases ↗",               ModRepoUrl + "/releases"),
            ("Steamodded wiki (installation) ↗",   SteamoddedWikiUrl),
            ("Lovely Injector releases ↗",          "https://github.com/nicholasgasior/lovely/releases"),
            ("Balatro on Steam ↗",                  "https://store.steampowered.com/app/2379780/Balatro/"),
            ("Archipelago Official ↗",             "https://archipelago.gg"),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content         = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding         = new Thickness(0, 2, 0, 2),
                Background      = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize        = 12,
                Margin          = new Thickness(0, 0, 0, 4),
                Foreground      = linkClr,
                Cursor          = System.Windows.Input.Cursors.Hand,
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
                    Version: el.TryGetProperty("tag_name", out var t) ? t.GetString()  ?? "" : "",
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var u) ? u.GetString()       : null
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// Detect Balatro's Steam install directory via the Windows registry and
    /// Steam's appmanifest ACF file. Returns null if not found or not installed.
    private static string? DetectSteamGameDir()
    {
        try
        {
            // Read Steam root from registry.
            string? steamRoot = null;
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                       @"Software\Valve\Steam"))
            {
                steamRoot = key?.GetValue("SteamPath") as string;
            }
            if (string.IsNullOrEmpty(steamRoot)) return null;

            // Try the default library first.
            string defaultLib = Path.Combine(steamRoot, "steamapps");
            string? found = TryResolveFromLibrary(defaultLib);
            if (found != null) return found;

            // Parse libraryfolders.vdf for additional library paths.
            string vdf = Path.Combine(defaultLib, "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                foreach (string line in File.ReadAllLines(vdf))
                {
                    string trimmed = line.Trim();
                    // Valve VDF lines: "path"  "C:\\Steam\\library"
                    if (!trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase))
                        continue;
                    int q1 = trimmed.IndexOf('"', 6);
                    if (q1 < 0) continue;
                    int q2 = trimmed.IndexOf('"', q1 + 1);
                    int q3 = trimmed.IndexOf('"', q2 + 1);
                    if (q2 < 0 || q3 < 0) continue;
                    string libPath = trimmed[(q2 + 1)..q3].Replace("\\\\", "\\");
                    string? r = TryResolveFromLibrary(Path.Combine(libPath, "steamapps"));
                    if (r != null) return r;
                }
            }
        }
        catch { }
        return null;
    }

    /// Try to find Balatro inside a Steam library folder via appmanifest_2379780.acf.
    private static string? TryResolveFromLibrary(string steamappsDir)
    {
        try
        {
            string acf = Path.Combine(steamappsDir, $"appmanifest_{STEAM_APPID}.acf");
            if (!File.Exists(acf)) return null;

            // Parse the "installdir" key from the ACF (Valve key-value text).
            foreach (string line in File.ReadAllLines(acf))
            {
                string t = line.Trim();
                if (!t.StartsWith("\"installdir\"", StringComparison.OrdinalIgnoreCase))
                    continue;
                int q1 = t.IndexOf('"', 12);
                if (q1 < 0) continue;
                int q2 = t.IndexOf('"', q1 + 1);
                int q3 = t.IndexOf('"', q2 + 1);
                if (q2 < 0 || q3 < 0) continue;
                string installDir = t[(q2 + 1)..q3];
                string fullPath = Path.Combine(steamappsDir, "common", installDir);
                return Directory.Exists(fullPath) ? fullPath : null;
            }
        }
        catch { }
        return null;
    }

    /// True if Lovely Injector's version.dll is present in the Balatro game dir.
    /// (version.dll is what loads Steamodded and all mods via the Lovely injector.)
    private bool IsSteamoddedInstalled()
    {
        string gameDir = GameDirectory;
        if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
            return false;
        return File.Exists(Path.Combine(gameDir, "version.dll"));
    }

    /// Find the Balatro exe for direct launch (fallback to Steam URI).
    private string? FindBalatroDirect()
    {
        string gameDir = GameDirectory;
        if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
            return null;

        // Common exe names.
        foreach (string name in new[] { "Balatro.exe", "balatro.exe" })
        {
            string path = Path.Combine(gameDir, name);
            if (File.Exists(path)) return path;
        }

        // Fuzzy fallback.
        try
        {
            foreach (string exe in Directory.EnumerateFiles(gameDir, "*.exe",
                         SearchOption.TopDirectoryOnly))
            {
                string lower = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (lower.Contains("balatro")) return exe;
            }
        }
        catch { }
        return null;
    }

    /// Pre-write APSettings.json with the AP session credentials.
    /// Format verified from randomizer.lua: all fields are JSON strings
    /// (including APPort, which Balatro stores as a string).
    private static void WriteApSettings(ApSession session)
    {
        var (host, port) = ParseServerHostPort(session.ServerUri);

        string dir = Path.GetDirectoryName(ApSettingsPath)!;
        Directory.CreateDirectory(dir);

        var settings = new Dictionary<string, string>
        {
            ["APAddress"]  = host,
            ["APPort"]     = port.ToString(),
            ["APSlot"]     = session.SlotName,
            ["APPassword"] = session.Password ?? "",
        };

        File.WriteAllText(ApSettingsPath,
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    /// Blank the password in APSettings.json once the session ends.
    private static void ScrubApSettingsPassword()
    {
        try
        {
            if (!File.Exists(ApSettingsPath)) return;
            string text = File.ReadAllText(ApSettingsPath);
            if (string.IsNullOrWhiteSpace(text)) return;
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(text);
            if (dict == null) return;
            dict["APPassword"] = "";
            File.WriteAllText(ApSettingsPath,
                JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { }
    }

    /// Resolve the latest BalatroAP release: (tag, BalatroAP.zip URL).
    private async Task<(string Version, string? ZipUrl)>
        ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? t.GetString()?.Trim()
                : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;
                    // Asset is "BalatroAP.zip" (verified across all releases).
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                        && name.IndexOf("balatro", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return (version, url);
                    }
                }
                return (version, null);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        return (FallbackVersion, null);
    }

    /// Launch Balatro via direct exe or Steam URI fallback.
    private void StartGame()
    {
        IsRunning = true;

        string? exe = FindBalatroDirect();
        ProcessStartInfo psi;

        if (exe != null)
        {
            psi = new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute  = false,
            };
        }
        else
        {
            // Fallback: launch via Steam URI (works even if install dir unknown).
            psi = new ProcessStartInfo(
                $"steam://rungameid/{STEAM_APPID}")
            {
                UseShellExecute = true,
            };
        }

        try
        {
            var proc = Process.Start(psi);
            _gameProcess = proc;

            if (proc != null)
            {
                proc.EnableRaisingEvents = true;
                proc.Exited += (_, _) =>
                {
                    IsRunning = false;
                    ScrubApSettingsPassword();
                    GameExited?.Invoke(proc.ExitCode);
                };
            }
            else
            {
                // Steam URI launch: no process handle — assume running until user
                // dismisses or times out.
                IsRunning = false;
                GameExited?.Invoke(0);
            }
        }
        catch (Exception ex)
        {
            IsRunning = false;
            throw new InvalidOperationException(
                "Could not launch Balatro. Make sure the game is installed via Steam.", ex);
        }
    }

    /// Parse "host:port", "ws://host:port", etc. Default AP port = 38281.
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
                if (rest.StartsWith(':') && int.TryParse(rest[1..], out int p6)
                    && p6 > 0 && p6 <= 65535) port = p6;
            }
        }
        else if (colonCount > 1)
        {
            host = s;
        }
        else
        {
            int colon = s.LastIndexOf(':');
            if (colon > 0 && int.TryParse(s[(colon + 1)..], out int p)
                && p > 0 && p <= 65535)
            {
                host = s[..colon];
                port = p;
            }
        }

        if (host.Length == 0) host = "archipelago.gg";
        return (host, port);
    }

    // ── Small UI factory helpers ──────────────────────────────────────────────

    private static System.Windows.Controls.TextBlock SectionHeader(
        string text, System.Windows.Media.Brush muted)
        => new()
        {
            Text = text, FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin = new Thickness(0, 8, 0, 8),
        };

    private static System.Windows.Controls.Button MakeButton(
        string label,
        System.Windows.Media.Brush fg,
        double width)
        => new()
        {
            Content     = label,
            Width       = width,
            Padding     = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
}
