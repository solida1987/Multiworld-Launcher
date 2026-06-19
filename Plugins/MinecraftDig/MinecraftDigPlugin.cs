using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LauncherV2.Core;

// NOTE on type qualification (BUILD GOTCHA — CS0104):
// This project sets BOTH <UseWPF>true</UseWPF> and <UseWindowsForms>true</UseWindowsForms>.
// WPF UI types are FULLY QUALIFIED below to avoid CS0104. Do NOT add
// `using System.Windows.Controls;` or `using System.Windows.Media;` here.

namespace LauncherV2.Plugins.MinecraftDig;

// ═══════════════════════════════════════════════════════════════════════════════
// MinecraftDigPlugin — install / launch for "Minecraft Dig", a distinct
// Archipelago world from standard Minecraft.
//
// ── VERIFIED FACTS (2026-06-19, sources: jacobmix/Minecraft_AP_Randomizer) ───
//
//   * GAME: Minecraft Java Edition 1.19.4 with the Dig server-side Forge mod.
//     "Minecraft Dig" is a standalone variant where the player digs out a chunk
//     of the world layer by layer; each layer is a check. Completely separate
//     from the standard Minecraft AP world (which targets MC 1.20.4 / NeoForge).
//
//   * AP WORLD: apworld file is "minecraft_dig.apworld" (verified from release
//     assets tag 0.0.11_dig). AP game string: "Minecraft Dig".
//     Repository: jacobmix/Minecraft_AP_Randomizer (community fork).
//     Latest verified release: v0.0.11 (tag 0.0.11_dig, MC 1.19.4).
//     Release assets: aprandomizer-MC1.19.4-0.0.11.jar + minecraft_dig.apworld.
//
//   * PATCH FILE EXTENSION: .apmcdig (instead of .apmc used by standard Minecraft).
//
//   * MINECRAFT VERSION: Java 1.19.4. Requires the player to own Minecraft Java
//     Edition. The mod uses the legacy Forge loader (not NeoForge).
//
//   * ConnectsItself = true: the Forge server-side mod handles the AP connection
//     directly. The launcher must NOT hold a competing ApClient on the same slot.
//
//   * SupportsStandalone = false: Minecraft Dig is designed exclusively for
//     Archipelago multiworld play.
//
//   * LAUNCH PATTERN: same as standard Minecraft — a LOCAL dedicated Forge server
//     runs on the player's machine; the player connects their own Minecraft 1.19.4
//     Java client to localhost. The mod auto-connects to AP.
//     The .apmcdig file (provided by the AP host) goes in the APData/ folder.
//
//   * GAME NOT INCLUDED — the player must own Minecraft Java Edition.
//     This plugin downloads the Dig mod jar; the player sets up Forge + server.
//
//   * BUILD NOTE: UseWindowsForms=true alongside UseWPF=true; all WPF UI types
//     are fully qualified to avoid CS0104 ambiguity.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class MinecraftDigPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GH_OWNER    = "jacobmix";
    private const string GH_REPO     = "Minecraft_AP_Randomizer";
    private const string GH_RELEASES = $"https://api.github.com/repos/{GH_OWNER}/{GH_REPO}/releases";
    private const string REPO_URL    = $"https://github.com/{GH_OWNER}/{GH_REPO}";
    private const string MC_DOWNLOAD_URL = "https://www.minecraft.net/en-us/download";

    /// Pinned fallback release tag and asset URLs (verified 2026-06-19).
    private const string FALLBACK_TAG     = "0.0.11_dig";
    private const string FALLBACK_JAR_URL =
        "https://github.com/jacobmix/Minecraft_AP_Randomizer/releases/download/" +
        "0.0.11_dig/aprandomizer-MC1.19.4-0.0.11.jar";
    private const string FALLBACK_APWORLD_URL =
        "https://github.com/jacobmix/Minecraft_AP_Randomizer/releases/download/" +
        "0.0.11_dig/minecraft_dig.apworld";

    private const string MOD_JAR_FILENAME   = "aprandomizer-dig.jar";
    private const string APDATA_FOLDER_NAME = "APData";
    private const string VERSION_STAMP_FILE = "ap_dig_mod_version.dat";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(20),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "minecraft_dig";
    public string DisplayName => "Minecraft Dig";
    public string Subtitle    => "Native PC · Forge server + Dig AP mod";

    /// EXACT AP game string — verified via apworld filename "minecraft_dig.apworld"
    /// from jacobmix/Minecraft_AP_Randomizer. Distinct from "Minecraft".
    public string ApWorldName => "Minecraft Dig";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "minecraft_dig.png");

    public string ThemeAccentColor => "#8B6914";   // Dirt/earth brown

    public string[] GameBadges => new[] { "Own Minecraft 1.19.4 · Forge" };

    public string Description =>
        "Minecraft Dig is an Archipelago world where you dig out a chunk of the " +
        "world layer by layer — each layer is a location check. Items from the " +
        "multiworld appear as pickaxes, scaffolding, and other goodies (or traps!) " +
        "along the way. This is a completely separate AP world from standard Minecraft, " +
        "running on Minecraft Java 1.19.4 with a legacy Forge server-side mod. " +
        "You need your own copy of Minecraft Java Edition. The launcher downloads " +
        "the Dig server mod for you. Your host provides a .apmcdig data file which " +
        "you place in the APData folder. The mod connects to the Archipelago server " +
        "automatically when you join the local server. " +
        "Community APWorld by jacobmix/Minecraft_AP_Randomizer.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version / install state ───────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    public bool IsInstalled =>
        Directory.Exists(ModsDir) &&
        File.Exists(Path.Combine(ModsDir, MOD_JAR_FILENAME));

    public bool IsRunning { get; private set; }

    public bool ConnectsItself     => true;
    public bool SupportsStandalone => false;

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory { get; set; } = string.Empty;

    private string ServerDir => Path.Combine(AppContext.BaseDirectory, "Games", "MinecraftDig", "server");
    private string ModsDir   => Path.Combine(ServerDir, "mods");
    private string ApDataDir => Path.Combine(ServerDir, APDATA_FOLDER_NAME);

    // ── AP bridge events ──────────────────────────────────────────────────────
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
    public event Action<int>?    GameExited;
#pragma warning restore CS0067

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        string stampPath = Path.Combine(ServerDir, VERSION_STAMP_FILE);
        InstalledVersion = File.Exists(stampPath)
            ? (await File.ReadAllTextAsync(stampPath, ct)).Trim()
            : null;

        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES, ct);
            using var doc = JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("tag_name", out var tag)) continue;
                string? t = tag.GetString();
                // Only consider "dig" releases (not the main minecraft releases)
                if (t != null && t.Contains("dig", StringComparison.OrdinalIgnoreCase))
                { AvailableVersion = t; break; }
            }
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(ModsDir);
        Directory.CreateDirectory(ApDataDir);

        // Resolve latest dig release asset URLs
        progress.Report((5, "Fetching latest Minecraft Dig release..."));
        string jarUrl     = FALLBACK_JAR_URL;
        string apworldUrl = FALLBACK_APWORLD_URL;
        string releaseTag = FALLBACK_TAG;

        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES, ct);
            using var doc = JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("tag_name", out var tagProp)) continue;
                string? tag = tagProp.GetString();
                if (tag == null || !tag.Contains("dig", StringComparison.OrdinalIgnoreCase)) continue;
                releaseTag = tag;
                if (el.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        string? name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                        string? url  = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                        if (name == null || url == null) continue;
                        if (name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
                            name.Contains("randomizer", StringComparison.OrdinalIgnoreCase))
                            jarUrl = url;
                        if (name.Equals("minecraft_dig.apworld", StringComparison.OrdinalIgnoreCase))
                            apworldUrl = url;
                    }
                }
                break;
            }
        }
        catch { /* use pinned fallback */ }

        // Download the Dig server mod jar
        progress.Report((20, $"Downloading Dig mod ({releaseTag})..."));
        await DownloadFileAsync(jarUrl, Path.Combine(ModsDir, MOD_JAR_FILENAME), progress, 20, 65, ct);

        // Download the apworld for reference / manual install into AP
        progress.Report((65, "Downloading minecraft_dig.apworld..."));
        await DownloadFileAsync(apworldUrl, Path.Combine(ServerDir, "minecraft_dig.apworld"),
            progress, 65, 85, ct);

        // Write setup notes
        progress.Report((85, "Writing setup notes..."));
        await File.WriteAllTextAsync(
            Path.Combine(ServerDir, "SETUP_NOTES.txt"),
            $"Minecraft Dig AP Mod — {releaseTag}\r\n" +
            "===========================================\r\n\r\n" +
            $"Mod jar:    mods\\{MOD_JAR_FILENAME}\r\n" +
            $"APData dir: {APDATA_FOLDER_NAME}\\\r\n\r\n" +
            "SETUP STEPS:\r\n" +
            "1. Install Minecraft Java 1.19.4 (from minecraft.net — you must own it).\r\n" +
            "2. Install legacy Forge for 1.19.4 from https://files.minecraftforge.net/\r\n" +
            "3. Copy the mod jar from mods\\ into your Forge 1.19.4 server mods\\ folder.\r\n" +
            "4. Get your .apmcdig file from your AP host and place it in APData\\\r\n" +
            "5. Run the Forge 1.19.4 server. Connect Minecraft 1.19.4 to localhost.\r\n" +
            "6. The mod auto-connects to the Archipelago server.\r\n\r\n" +
            $"More info: {REPO_URL}\r\n", ct);

        // Write version stamp
        await File.WriteAllTextAsync(Path.Combine(ServerDir, VERSION_STAMP_FILE), releaseTag, ct);
        InstalledVersion = releaseTag;
        progress.Report((100, $"Minecraft Dig {releaseTag} installed. See SETUP_NOTES.txt in {ServerDir}"));
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
        _ = session;
        OpenServerDirectory();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        OpenServerDirectory();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        IsRunning = false;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (Forge mod handles the AP connection) ──────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var linkClr = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF));

        var panel = new System.Windows.Controls.StackPanel
                    { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        panel.Children.Add(MakeHeader("ABOUT MINECRAFT DIG", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Minecraft Dig is a distinct AP world from standard Minecraft. " +
                   "Dig a chunk layer by layer — each layer = one check. " +
                   "Requires Minecraft Java 1.19.4 with legacy Forge. " +
                   "The launcher downloads the server-side Dig mod for you.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        panel.Children.Add(MakeHeader("SETUP STEPS", muted));
        foreach (string step in new[]
        {
            "1. Click Install/Update to download the Dig mod jar.",
            "2. Obtain Minecraft Java 1.19.4 (you must own it from minecraft.net).",
            "3. Install legacy Forge for 1.19.4 from files.minecraftforge.net.",
            "4. Copy the mod jar from server/mods/ into your Forge server's mods/ folder.",
            "5. Place your .apmcdig file (from your AP host) in server/APData/.",
            "6. Run the Forge 1.19.4 server and connect Minecraft 1.19.4 to localhost.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 4),
            });
        }

        panel.Children.Add(MakeHeader("SERVER DIRECTORY", muted));
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text       = ServerDir,
            IsReadOnly = true,
            FontSize   = 11,
            Background  = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        };
        panel.Children.Add(dirBox);

        panel.Children.Add(MakeHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("Minecraft Dig GitHub ↗",   REPO_URL),
            ("Latest Dig Release ↗",     REPO_URL + "/releases"),
            ("Minecraft Download ↗",     MC_DOWNLOAD_URL),
            ("Forge Loader ↗",           "https://files.minecraftforge.net/"),
            ("Archipelago Official ↗",   "https://archipelago.gg"),
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
                Foreground          = linkClr,
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
            string json = await _http.GetStringAsync(GH_RELEASES, ct);
            using var doc = JsonDocument.Parse(json);
            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("tag_name", out var tagProp)) continue;
                string? tag = tagProp.GetString();
                // Only surface dig releases in the news feed
                if (tag == null || !tag.Contains("dig", StringComparison.OrdinalIgnoreCase)) continue;

                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);
                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : tag,
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: tag,
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

    private void OpenServerDirectory()
    {
        IsRunning = true;
        try
        {
            string dir = Directory.Exists(ServerDir) ? ServerDir : AppContext.BaseDirectory;
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"")
                { UseShellExecute = true });
        }
        catch { }

        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            System.Windows.MessageBox.Show(
                "To play Minecraft Dig:\r\n\r\n" +
                "1. Run your Forge 1.19.4 server with the Dig mod jar in mods/.\r\n" +
                "2. Ensure your .apmcdig file is in APData/.\r\n" +
                "3. Connect Minecraft 1.19.4 to localhost in-game.\r\n" +
                "4. The mod auto-connects to the Archipelago server.\r\n\r\n" +
                $"Server directory: {ServerDir}",
                "Minecraft Dig — Start Instructions",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            IsRunning = false;
            GameExited?.Invoke(0);
        });
    }

    private async Task DownloadFileAsync(
        string url, string destPath,
        IProgress<(int Pct, string Msg)> progress,
        int startPct, int endPct,
        CancellationToken ct)
    {
        using var response = await _http.GetAsync(url,
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? -1;
        using var src  = await response.Content.ReadAsStreamAsync(ct);
        using var dest = File.Create(destPath);
        var buf  = new byte[81920];
        long done = 0;
        int  read;
        while ((read = await src.ReadAsync(buf, ct)) > 0)
        {
            await dest.WriteAsync(buf.AsMemory(0, read), ct);
            done += read;
            if (total > 0)
            {
                int pct = startPct + (int)((endPct - startPct) * done / total);
                progress.Report((pct, $"Downloading... {done / 1024:N0} KB"));
            }
        }
    }

    private static System.Windows.Controls.TextBlock MakeHeader(string text,
        System.Windows.Media.Brush color) => new()
    {
        Text       = text,
        FontSize   = 10,
        FontWeight = System.Windows.FontWeights.SemiBold,
        Foreground = color,
        Margin     = new System.Windows.Thickness(0, 8, 0, 8),
    };
}
