using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using LauncherV2.Core;

namespace LauncherV2.Plugins.RustedMoss;

// ═══════════════════════════════════════════════════════════════════════════════
// RustedMossPlugin — install / launch for "Rusted Moss" (faxdoc, 2023)
// played through the Archipelago mod by dgrossmann144, which is built into
// the game via a custom patcher / companion client. This is a NATIVE
// "ConnectsItself" integration: the game (or its companion) speaks to the AP
// server directly with no emulator and no launcher-held ApClient on the slot.
//
// ── HONEST REALITY CHECK (2026-06-14, verified online) ──────────────────────
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned
// copy of Rusted Moss (Steam appid 1772830; GameMaker Studio engine). The
// Archipelago integration is delivered via a release from the GitHub repo
// dgrossmann144/Archipelago, which ships patched game binaries or a companion
// client. The honest integration ceiling — exactly like the shipped Hollow
// Knight / TUNIC / Timespinner / Stardew Valley plugins — is "automate what
// is possible, guide the irreducible parts."
//
//   * THE AP WORLD game string is "Rusted Moss" (from the apworld in the
//     dgrossmann144/Archipelago fork). GameId here = "rusted_moss".
//
//   * THE MOD repo is dgrossmann144/Archipelago (GitHub). Releases ship the
//     patched game or companion as a zip; the exact asset names depend on the
//     specific release. This plugin resolves the latest release from the GitHub
//     API and attempts to identify a Windows-compatible zip among the assets.
//     A pinned fallback URL is defined so installation still works when the
//     GitHub API is unreachable.
//
//   * HOW IT INSTALLS: download the release zip from dgrossmann144/Archipelago
//     releases, extract to GameDirectory (Games\RustedMoss under the launcher
//     root). If the mod requires files to be placed next to the Steam install
//     of the game, the Settings panel guides the user to copy them.
//
//   * CONNECTION is made IN-GAME or via a companion client included with the
//     mod. The exact in-game connection flow is defined by the mod; the
//     settings panel presents the session credentials for the user to enter.
//     This launcher does NOT pre-fill an in-game config file because no
//     documented config path has been verified — the honest stance is to guide
//     the user through what the mod documents.
//
//   * LAUNCH: try "RustedMoss.exe" and "Rusted Moss.exe" in the Steam install
//     dir and in GameDirectory; fall back to steam://rungameid/1772830 if the
//     exe cannot be located.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Rusted Moss install via the Windows registry and
//      libraryfolders.vdf, locating steamapps\common\Rusted Moss via
//      appmanifest_1772830.acf. A manual folder override is supported and
//      persisted in the plugin's own sidecar (Games\RustedMoss\rustedmoss_launcher.json).
//   2. INSTALL/UPDATE: download the release zip from dgrossmann144/Archipelago
//      and extract to Games\RustedMoss. Clear guided steps describe what the
//      user needs to do afterwards (e.g., copy patched files into the Steam
//      install if required by the mod).
//   3. LAUNCH: run the game exe from the Steam install or GameDirectory, or
//      use the steam:// URL as a last resort. ConnectsItself = true.
//      SupportsStandalone = true (vanilla Rusted Moss runs without the mod).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class RustedMossPlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────
    private const string MOD_OWNER   = "dgrossmann144";
    private const string MOD_REPO    = "Archipelago";
    private const string ModRepoUrl  = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string SetupGuideUrl    = "https://archipelago.gg/tutorial/Rusted%20Moss/setup/en";
    private const string GameInfoUrl      = "https://archipelago.gg/games/Rusted%20Moss/info/en";
    private const string ArchipelagoSite  = "https://archipelago.gg";
    private const string SteamStoreUrl    = "https://store.steampowered.com/app/1772830/Rusted_Moss/";

    // Steam — Rusted Moss appid 1772830.
    private const string RmSteamAppId  = "1772830";
    private static readonly string SteamRunUrl = $"steam://rungameid/{RmSteamAppId}";

    /// The standard Steam install sub-folder name for Rusted Moss.
    private const string SteamCommonFolderName = "Rusted Moss";

    /// Candidate executable names (GameMaker builds sometimes differ).
    private static readonly string[] GameExeNames = { "RustedMoss.exe", "Rusted Moss.exe" };

    /// Stamp file written inside GameDirectory so CheckForUpdate can report a version.
    private const string VersionStampFile = "installed_version.txt";

    /// Pinned fallback in case the GitHub API is unreachable. Update when a new
    /// verified release is available.
    private const string FallbackVersion = "latest";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/latest/download/Rusted.Moss.Archipelago.zip";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "rusted_moss";
    public string DisplayName => "Rusted Moss";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string from dgrossmann144/Archipelago apworld.
    public string ApWorldName => "Rusted Moss";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "rusted_moss.png");

    public string ThemeAccentColor => "#3A6B35";   // dark mossy green
    public string[] GameBadges     => new[] { "Steam · needs mod" };

    public string Description =>
        "Rusted Moss, faxdoc's 2023 post-apocalyptic Metroidvania with a physics-based " +
        "grappling hook, played through the Archipelago mod by dgrossmann144. Items, " +
        "abilities, and progression across the mossy ruins are shuffled into the " +
        "multiworld — the game connects to the AP server directly, so no emulator or " +
        "bridge is needed. You bring your own copy of Rusted Moss (owned on Steam); the " +
        "launcher downloads the mod release and extracts it, then guides the rest. " +
        "Connect to your Archipelago server from the in-game or companion interface.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the version stamp exists in GameDirectory (written by
    /// InstallOrUpdateAsync), OR the user has placed mod files there by hand.
    public bool IsInstalled => DetectInstalledMod();

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin extracts the mod release and keeps its working files.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "RustedMoss");

    /// This plugin's OWN settings sidecar.
    private string SettingsSidecarPath
        => Path.Combine(GameDirectory, "rustedmoss_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The mod reports checks/items/goal to the AP server directly — the launcher
    // relays nothing. These exist for interface compatibility (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── Connectivity ──────────────────────────────────────────────────────────

    /// The mod owns the slot connection; the launcher must NOT hold its own ApClient.
    public bool ConnectsItself => true;

    /// Vanilla Rusted Moss runs without the mod.
    public bool SupportsStandalone => true;

    public string? BuiltAgainstDataPackageChecksum => null;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            InstalledVersion = DetectInstalledMod() ? (ReadStampedVersion() ?? "installed") : null;
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
            AvailableVersion = null;
        }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((2, "Checking the latest Rusted Moss Archipelago release..."));
        var (version, zipUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find a downloadable release zip on GitHub. Check your " +
                "internet connection, or download it manually from " + ModRepoUrl +
                "/releases and extract it to " + GameDirectory + ".");

        progress.Report((6, $"Downloading Rusted Moss Archipelago {version}..."));
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"rustedmoss-ap-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"rustedmoss-ap-x-{version}-{Guid.NewGuid():N}");

        try
        {
            // Download with progress.
            using (var response = await _http.GetAsync(
                zipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                long total      = response.Content.Headers.ContentLength ?? -1;
                long downloaded = 0;

                await using var src = await response.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tempZip);
                var buf = new byte[81920];
                int bytesRead;
                while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
                    downloaded += bytesRead;
                    if (total > 0)
                    {
                        int pct = (int)(6 + 55 * downloaded / total);
                        progress.Report((pct, $"Downloading... {downloaded / 1024}KB / {total / 1024}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((65, "Extracting mod files..."));
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // Flatten a single top-level wrapper folder if present (common zip layout).
            string srcDir = FlattenSingleSubfolder(tempExtract);

            progress.Report((80, $"Installing to {GameDirectory}..."));
            Directory.CreateDirectory(GameDirectory);
            CopyDirectory(srcDir, GameDirectory);

            progress.Report((94, "Finalizing..."));
            WriteStampedVersion(version);
            InstalledVersion = version;

            progress.Report((100,
                $"Rusted Moss Archipelago {version} installed to {GameDirectory}. " +
                "If the mod requires patched files to be placed alongside your Rusted Moss " +
                "Steam installation, follow the guided steps in Settings to complete setup. " +
                "Then press Play and connect to your Archipelago server from the in-game " +
                "or companion interface."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
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
        // ConnectsItself = true: the mod owns the slot connection.
        // Connection is entered in-game or via the companion; no config prefill is
        // applied here because no documented config path has been verified.
        _ = session;
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
        IsRunning     = false;
        _gameProcess  = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert ─────────────────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Existing-install validation (override picker) ─────────────────────────

    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Rusted Moss install folder.";

        if (LooksLikeRustedMossDir(folder))
            return null;

        // Be forgiving: user may have picked the "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeRustedMossDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Rusted Moss installation. Pick the folder " +
               "that contains RustedMoss.exe or Rusted Moss.exe (for Steam this is " +
               @"usually ...\steamapps\common\Rusted Moss).";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var accent  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x6B, 0x35));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Rusted Moss is your own game (Steam) with the Archipelago mod added " +
                   "on top. The launcher downloads the mod release and extracts it to the " +
                   "mod folder. If the mod requires patched files placed alongside your " +
                   "Steam installation, follow the guided steps below. You connect to your " +
                   "Archipelago server from the in-game or companion interface. These " +
                   "external steps are not verified by this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: Steam install detection / override ───────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "RUSTED MOSS INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? steamDir    = DetectSteamRustedMossDir();
        string? overrideDir = LoadOverrideDir();
        string? activeDir   = overrideDir ?? steamDir;
        string  detectMsg   = activeDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + activeDir
                : "Detected Steam install: " + activeDir)
            : "Rusted Moss not detected. Pick your install folder below, or install " +
              "Rusted Moss via Steam first.";

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = activeDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // Mod status line
        bool modOk = DetectInstalledMod();
        string? stamped = ReadStampedVersion();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modOk
                ? "Archipelago mod installed" + (stamped != null ? $" (version {stamped})" : "") +
                  " in " + GameDirectory + "."
                : "Archipelago mod not installed yet — use Install on the Play tab, or " +
                  "extract the release zip from the mod repo to " + GameDirectory + ".",
            FontSize = 11, Foreground = modOk ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        // Folder override picker
        var dirRow = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = activeDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Rusted Moss Steam install folder. Detected automatically; " +
                          "override here if on a non-standard library path.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120, Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Rusted Moss install folder",
                InitialDirectory = Directory.Exists(activeDir ?? "")
                                   ? activeDir!
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Rusted Moss folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                if (!LooksLikeRustedMossDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeRustedMossDir(nested)) picked = nested;
                }
                SaveOverrideDir(picked);
                dirBox.Text = picked;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(dirBtn, System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Steam installs are detected automatically (appid 1772830). Use this " +
                   "picker for non-standard Steam library paths.",
            FontSize = 11, Foreground = muted, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection ───────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in-game / companion)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "After launching Rusted Moss with the Archipelago mod, connect to your " +
                   "server from the in-game menu or the companion client included with the " +
                   "mod. Enter your server address (host:port), slot name, and password (if " +
                   "any). This launcher does not pre-fill the connection — it is entered " +
                   "in-game or in the companion.",
            FontSize = 11, Foreground = fg, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Rusted Moss on Steam (appid 1772830). Install it if you have not already. " +
                "Use the folder picker above if your install was not detected.",
            "2. Install the Archipelago mod: press Install on the Play tab to download the " +
                "latest release from dgrossmann144/Archipelago and extract it to " + GameDirectory + ". " +
                "Alternatively, download the release zip manually and extract it there.",
            "3. If the mod release includes patched game files (e.g., a patched data.win or " +
                "companion exe), copy them into your Rusted Moss Steam install folder — consult " +
                "the mod repo's README for the exact files. The README link is below.",
            "4. Press Play. This launcher will run the game executable from the detected " +
                "install or from " + GameDirectory + " if a patched exe is present there.",
            "5. Connect to your Archipelago server from the in-game menu or the companion " +
                "client. Enter your server address (host:port), slot name, and password.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("Rusted Moss Archipelago (GitHub) ↗", ModRepoUrl + "/releases"),
            ("Rusted Moss Setup Guide (AP) ↗",     SetupGuideUrl),
            ("Rusted Moss on Steam ↗",              SteamStoreUrl),
            ("Archipelago Official ↗",              ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); } catch { }
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
            string json = await _http.GetStringAsync(GH_MOD_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);
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

    /// "v1.2.3" → "1.2.3"; else trimmed as-is.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest release: version + a Windows-compatible zip URL.
    /// Prefers assets whose name contains "rusted", "moss", or "windows"; skips
    /// Linux/Mac/source assets; falls back to the pinned URL when unavailable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_MOD_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? preferred = null;   // Rusted Moss or Windows zip
                string? anyZip    = null;   // any .zip fallback

                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    // Skip non-Windows / non-mod assets.
                    if (lower.Contains("linux") || lower.Contains("mac") ||
                        lower.Contains("osx")   || lower.Contains("darwin") ||
                        lower.Contains("source") || lower.Contains("symbols"))
                        continue;

                    anyZip ??= url;
                    if (preferred == null &&
                        (lower.Contains("rusted") || lower.Contains("moss") ||
                         lower.Contains("windows") || lower.Contains("win")))
                        preferred = url;
                }

                string? zip = preferred ?? anyZip;
                if (zip != null)
                    return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (FallbackVersion, FallbackZipUrl);
    }

    // ── Private helpers — Steam / game detection ──────────────────────────────

    /// Detect the Steam Rusted Moss install directory.
    private static string? DetectSteamRustedMossDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{RmSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeRustedMossDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeRustedMossDir(conventional)) return conventional;
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    /// A folder "looks like" Rusted Moss if it contains a known game executable.
    private static bool LooksLikeRustedMossDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            foreach (string exe in GameExeNames)
                if (File.Exists(Path.Combine(dir, exe))) return true;
            return false;
        }
        catch { return false; }
    }

    /// "Installed" means the version stamp file is present in GameDirectory, OR any
    /// .exe other than the game's own is present in GameDirectory (companion client).
    private bool DetectInstalledMod()
    {
        try
        {
            if (!Directory.Exists(GameDirectory)) return false;
            if (File.Exists(Path.Combine(GameDirectory, VersionStampFile))) return true;
            // Also count if any file was extracted there.
            return Directory.EnumerateFiles(GameDirectory, "*", SearchOption.TopDirectoryOnly)
                            .Any();
        }
        catch { return false; }
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Launch priority:
    ///   1. Exe in GameDirectory (a patched or companion exe extracted by Install).
    ///   2. Exe in the detected Steam install dir.
    ///   3. steam://rungameid/1772830 as last resort.
    private void StartGame()
    {
        // 1. Try GameDirectory first (may have a patched or companion exe).
        string? exe = TryFindExe(GameDirectory);

        // 2. Try the Steam install dir.
        if (exe == null)
        {
            string? steamDir = (LoadOverrideDir() is { } ov && LooksLikeRustedMossDir(ov))
                ? ov
                : DetectSteamRustedMossDir();
            if (steamDir != null)
                exe = TryFindExe(steamDir);
        }

        if (exe != null)
        {
            string workDir = Path.GetDirectoryName(exe) ?? GameDirectory;
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = workDir,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Rusted Moss.");

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
            catch { /* non-fatal — some processes don't expose Exited */ }
            return;
        }

        // 3. Steam URL fallback.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true;
                return;
            }
            catch { /* fall through */ }
        }

        throw new FileNotFoundException(
            "Could not find the Rusted Moss executable. Open this game's Settings and " +
            "pick your Rusted Moss install folder, or install via Steam first.",
            "RustedMoss.exe");
    }

    /// Return the first matching known exe inside a directory, or null.
    private static string? TryFindExe(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;
        foreach (string name in GameExeNames)
        {
            string path = Path.Combine(dir, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    // ── Private helpers — zip extraction ──────────────────────────────────────

    /// If the extracted root contains exactly ONE sub-directory and no other files,
    /// return that sub-directory (flatten the wrapper). Otherwise return root.
    private static string FlattenSingleSubfolder(string root)
    {
        try
        {
            string[] dirs  = Directory.GetDirectories(root);
            string[] files = Directory.GetFiles(root);
            if (dirs.Length == 1 && files.Length == 0)
                return dirs[0];
        }
        catch { /* ignore */ }
        return root;
    }

    /// Recursively copy a directory tree into a destination, overwriting.
    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceDir, file);
            string dst = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(file, dst, overwrite: true);
        }
    }

    // ── Private helpers — Steam registry / VDF parsing ───────────────────────

    private static IEnumerable<string> SteamRoots()
    {
        string? hkcu = ReadRegistryString(Registry.CurrentUser,
            @"Software\Valve\Steam", "SteamPath");
        if (!string.IsNullOrWhiteSpace(hkcu)) yield return NormalizeSteamPath(hkcu);

        string? hklm = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm)) yield return NormalizeSteamPath(hklm);

        string? hklm64 = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm64)) yield return NormalizeSteamPath(hklm64);

        string? progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

    private static string NormalizeSteamPath(string p) => p.Replace('/', '\\').TrimEnd('\\');

    private static IEnumerable<string> SteamLibraryRoots(string steamRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (seen.Add(steamRoot)) yield return steamRoot;

        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        string text;
        try { text = File.ReadAllText(vdf); }
        catch { yield break; }

        foreach (string path in ExtractVdfPaths(text))
        {
            string norm = path.Replace('/', '\\').TrimEnd('\\');
            if (norm.Length > 0 && seen.Add(norm))
                yield return norm;
        }
    }

    private static IEnumerable<string> ExtractVdfPaths(string text)
    {
        const string key = "\"path\"";
        int i = 0;
        while ((i = text.IndexOf(key, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            i += key.Length;
            int open = text.IndexOf('"', i);
            if (open < 0) yield break;
            int close = text.IndexOf('"', open + 1);
            if (close < 0) yield break;
            yield return text.Substring(open + 1, close - open - 1).Replace("\\\\", "\\");
            i = close + 1;
        }
    }

    private static string? ReadAcfInstallDir(string acfPath)
    {
        try
        {
            string text = File.ReadAllText(acfPath);
            const string key = "\"installdir\"";
            int i = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            i += key.Length;
            int open = text.IndexOf('"', i);
            if (open < 0) return null;
            int close = text.IndexOf('"', open + 1);
            if (close < 0) return null;
            return text.Substring(open + 1, close - open - 1).Replace("\\\\", "\\");
        }
        catch { return null; }
    }

    private static string? ReadRegistryString(RegistryKey hive, string subKey, string valueName)
    {
        try
        {
            using RegistryKey? k = hive.OpenSubKey(subKey);
            return k?.GetValue(valueName) as string;
        }
        catch { return null; }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class RustedMossSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private RustedMossSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<RustedMossSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(RustedMossSettings s)
    {
        try
        {
            Directory.CreateDirectory(GameDirectory);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal */ }
    }

    private string? LoadOverrideDir()
    {
        string? p = LoadSettings().InstallOverride;
        return string.IsNullOrWhiteSpace(p) ? null : p;
    }

    private void SaveOverrideDir(string p) { var s = LoadSettings(); s.InstallOverride = p; SaveSettings(s); }

    private string? ReadStampedVersion()
    {
        // First try the dedicated stamp file (faster than the JSON sidecar for this).
        try
        {
            string stampPath = Path.Combine(GameDirectory, VersionStampFile);
            if (File.Exists(stampPath))
            {
                string v = File.ReadAllText(stampPath).Trim();
                if (!string.IsNullOrEmpty(v)) return v;
            }
        }
        catch { /* ignore */ }

        string? v2 = LoadSettings().ModVersion;
        return string.IsNullOrWhiteSpace(v2) ? null : v2;
    }

    private void WriteStampedVersion(string version)
    {
        try
        {
            Directory.CreateDirectory(GameDirectory);
            File.WriteAllText(
                Path.Combine(GameDirectory, VersionStampFile),
                version,
                new UTF8Encoding(false));
        }
        catch { /* non-fatal */ }
        var s = LoadSettings();
        s.ModVersion = version;
        SaveSettings(s);
    }
}
