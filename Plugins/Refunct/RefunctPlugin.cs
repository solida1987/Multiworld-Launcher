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
using Microsoft.Win32;
using LauncherV2.Core;

// The launcher project sets BOTH UseWPF and UseWindowsForms=true, so several UI
// type names (Color, Brushes, Button, TextBox, HorizontalAlignment) collide
// between WPF and WinForms. All WPF UI types are fully-qualified below
// (System.Windows.Controls.*, System.Windows.Media.*, etc.) to avoid CS0104.
// No file-level using aliases duplicating GlobalUsings (that is CS1537).

namespace LauncherV2.Plugins.Refunct;

// ═══════════════════════════════════════════════════════════════════════════════
// RefunctPlugin — install / update / launch for "Refunct" (Dominique Grieshofer,
// 2015) played through the Archipelago UE4 mod by spinerak.
//
// ── HONEST REALITY CHECK (2026-06-14) ─────────────────────────────────────────
// Refunct is a short, clean Unreal Engine 4 first-person parkour platformer
// (Steam appid 376030, ~1 hour to 100%). The Archipelago integration is a mod
// maintained by spinerak at github.com/spinerak/refunct-tas-archipelago. The
// mod ships its own in-game AP client, so the game connects to the multiworld
// itself (ConnectsItself = true) — the launcher must NOT hold its own ApClient
// on the same slot while the game runs.
//
// AP WORLD game string: "Refunct" (verified against the mod repo).
//
// MOD INSTALL — UE4 DLL injection:
//   The mod is distributed as a GitHub release zip from
//   github.com/spinerak/refunct-tas-archipelago/releases. The zip contains mod
//   files that are dropped into the game's directory (typically the root or the
//   Binaries/Win64 folder). Common UE4 modding patterns use a "dwmapi.dll"
//   proxy DLL in the Binaries/Win64 folder, or files placed next to the shipping
//   executable. This plugin downloads the release zip and extracts it, placing
//   files directly into the game's Binaries/Win64 folder (where the shipping
//   exe lives). A version stamp file ("refunct_ap_version.dat") is written after
//   a successful install.
//
// IsInstalled heuristic:
//   We check for our own stamp file (written on launcher-driven install), or for
//   a DLL named "dwmapi.dll" or any file whose name mentions "archipelago" in the
//   game's Binaries/Win64 folder — matching common UE4 proxy-mod patterns. This
//   also honors manual installs.
//
// LAUNCH:
//   The shipping exe is "Refunct-Win64-Shipping.exe" in Binaries\Win64\, or
//   "Refunct.exe" in the game root (if present). Falls back to
//   steam://rungameid/376030 when the exe is not found but Steam is installed.
//
// CONNECTION:
//   The in-game AP client in the mod reads connection details from a config
//   file or in-game UI. There is NO command-line or pre-written config mechanism
//   verified offline for this mod. The settings panel instructs the user how to
//   connect in-game and surfaces the session details to copy. No plaintext
//   password is written to disk by this plugin.
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time") ──────────────────────────
//   * RELEASE ASSET NAME: the exact Windows zip filename was not verified offline.
//     ResolveLatestReleaseAsync picks any .zip asset (no platform filter needed
//     as this is a single-platform UE4 mod). The first .zip wins; the pinned
//     fallback URL uses the generic tag pattern.
//   * MOD FILES: the exact file layout inside the release zip was not verified
//     offline. The installer extracts to Binaries\Win64\ and also flattens a
//     single top-level subfolder if the zip uses one.
//   * EXE NAME: "Refunct-Win64-Shipping.exe" is the standard UE4 shipping exe
//     name for this game (verified by Steam depot). A fallback scan for
//     "*Refunct*.exe" in Binaries\Win64\ handles renamed variants.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class RefunctPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GITHUB_OWNER = "spinerak";
    private const string GITHUB_REPO  = "refunct-tas-archipelago";
    private const string RepoUrl      = $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases";
    private const string GH_RELEASES_LATEST_URL = $"{GH_RELEASES_URL}/latest";

    private const string SetupGuideUrl   = "https://archipelago.gg/tutorial/Refunct/setup/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Refunct appid 376030.
    private const string RefunctSteamAppId   = "376030";
    private static readonly string SteamRunUrl = $"steam://rungameid/{RefunctSteamAppId}";

    /// Standard Steam install folder name for Refunct.
    private const string SteamCommonFolderName = "Refunct";

    /// The shipping executable (standard UE4 name, in Binaries\Win64\).
    private const string ShippingExeName = "Refunct-Win64-Shipping.exe";

    /// Pinned fallback version used when the GitHub API is unreachable.
    private const string FallbackVersion = "1.0.0";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    /// Installed-version stamp, written after a successful launcher-driven install.
    private const string VersionFileName = "refunct_ap_version.dat";

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "refunct";
    public string DisplayName => "Refunct";
    public string Subtitle    => "Native PC · UE4 Archipelago mod";

    /// EXACT AP world name as used by the spinerak/refunct-tas-archipelago world.
    public string ApWorldName => "Refunct";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "refunct.png");

    public string ThemeAccentColor => "#3AADCC";   // sky cyan — the game's clean minimalist palette
    public string[] GameBadges     => new[] { "Steam · needs mod" };

    public string Description =>
        "Refunct is a short, peaceful first-person parkour platformer by Dominique " +
        "Grieshofer — a serene, minimalist world of white platforms and cyan water " +
        "where you restore life to an archipelago by pressing buttons, leaping across " +
        "platforms, and sliding through portals. In Archipelago the buttons and " +
        "platforms become location checks shuffled across the multiworld. The mod by " +
        "spinerak ships its own in-game Archipelago client, so Refunct connects to " +
        "your multiworld server directly — no emulator, no bridge. You bring your own " +
        "copy of Refunct (owned on Steam); the Archipelago mod is installed on top " +
        "into the game's Binaries/Win64 folder. The launcher detects your Steam " +
        "install and can install the mod for you. You connect to your server from " +
        "inside the game.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" = mod files are present in the detected/override game install.
    /// We honour both launcher-driven installs (stamp file) and manual installs
    /// (presence of a proxy DLL or any file mentioning "archipelago" in Binaries\Win64).
    public bool IsInstalled => FindInstalledModIndicator() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Downloads and launcher bookkeeping go here. The actual mod files are
    /// extracted INTO the Refunct Steam install's Binaries\Win64 folder.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Refunct");

    /// This plugin's own settings sidecar (install-dir override + version stamp),
    /// kept separate from the shared SettingsStore so the plugin is self-contained.
    private string SidecarDir  => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SidecarPath => Path.Combine(SidecarDir, "refunct_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The mod's built-in AP client reports checks/items/goal to the server directly.
    // These events exist for IGamePlugin contract compliance (ConnectsItself = true).
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
            InstalledVersion = FindInstalledModIndicator() != null
                ? (ReadStampedVersion() ?? "installed")
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
        // 0. We need a Refunct Steam install to drop the mod into.
        progress.Report((2, "Locating your Refunct installation..."));
        string? gameDir = ResolveRefunctDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a Refunct installation. Open this game's Settings and " +
                "pick your Refunct folder (the one containing Refunct-Win64-Shipping.exe " +
                "or Refunct.exe), or install Refunct via Steam first. The Archipelago " +
                "mod is added on top of your own copy of the game.");

        // The mod's files go into Binaries\Win64\ next to the shipping exe.
        string binWin64 = Path.Combine(gameDir, "Binaries", "Win64");
        if (!Directory.Exists(binWin64))
        {
            // Some layouts have the shipping exe directly in the root.
            binWin64 = gameDir;
        }

        // 1. Resolve the latest GitHub release.
        progress.Report((6, "Checking the latest Refunct Archipelago mod release..."));
        var (version, zipUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the Refunct Archipelago mod download on GitHub. Check " +
                "your internet connection, or download it manually from " +
                RepoUrl + "/releases — see Settings for the guided steps.");

        // 2. Download and extract the mod zip into Binaries\Win64\.
        await DownloadAndExtractModAsync(zipUrl, version, binWin64, progress, ct);

        // 3. Stamp the version.
        WriteStampedVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            $"Refunct Archipelago mod {version} installed into your game's Binaries\\Win64 " +
            "folder. To connect: launch the game, and use the in-game Archipelago UI to " +
            "enter your Server, Port, Slot Name and Password. See Settings for the " +
            "guided steps and links."));
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
        // HONEST: there is no verified command-line or config-file mechanism this
        // launcher can use to pre-fill the Refunct AP mod's connection details.
        // Launching from this tile starts the game; the user connects in-game.
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism available
        StartRefunct();
        return Task.CompletedTask;
    }

    /// Refunct is a standalone game — it runs fine without AP.
    public bool SupportsStandalone => true;

    /// The mod's built-in AP client owns the slot connection.
    /// The launcher must not connect its own ApClient to the same slot.
    public bool ConnectsItself => true;

    public string? BuiltAgainstDataPackageChecksum => null;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartRefunct();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        // No plaintext AP password is written by this plugin — nothing to scrub.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true, see header) ────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The mod's built-in AP client receives items from the server directly;
        // there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game; no launcher HUD channel.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Return null when the folder looks like a valid Refunct install; else a
    /// short reason. Used by the Settings folder picker.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Refunct install folder.";

        if (LooksLikeRefunctDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeRefunctDir(nested)) return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Refunct installation. Pick the folder that " +
               "contains Refunct-Win64-Shipping.exe or Refunct.exe — for Steam this is " +
               @"usually ...\steamapps\common\Refunct.";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var accent  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0xAD, 0xCC));
        var panel   = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20),
        };

        // ── Install status ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "REFUNCT INSTALL",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? gameDir     = ResolveRefunctDir();
        string? overrideDir = LoadOverrideDir();
        string? modFile     = FindInstalledModIndicator();

        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Refunct not detected. Pick your install folder below, or install " +
              "Refunct via Steam first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text         = detectMsg,
            FontSize     = 11,
            Foreground   = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 6),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text         = modFile != null
                            ? "Archipelago mod found: " + modFile
                            : "Archipelago mod not found yet (use Install on the Play tab, " +
                              "or install it by hand from the mod releases).",
            FontSize     = 11,
            Foreground   = modFile != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // ── Install folder picker ─────────────────────────────────────────
        var dirRow = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text        = overrideDir ?? gameDir ?? "",
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Refunct install folder (containing Refunct-Win64-Shipping.exe). " +
                          "Detected from Steam automatically; set it here to override " +
                          "(non-standard Steam library, or another store).",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content     = "Select folder...",
            Width       = 120,
            Padding     = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Refunct install folder",
                InitialDirectory = Directory.Exists(overrideDir ?? gameDir ?? "")
                                   ? (overrideDir ?? gameDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Refunct folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the Steam "common" parent, descend.
                if (!LooksLikeRefunctDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeRefunctDir(nested)) picked = nested;
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
            Text         = "Steam installs are detected automatically (appid 376030). Use this " +
                           "picker for a non-standard Steam library or another store.",
            FontSize     = 11,
            Foreground   = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 6, 0, 14),
        });

        // ── Connecting in-game ────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "CONNECTING (done in-game)",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text         = "After installing the mod and launching the game, use the in-game " +
                           "Archipelago menu or console to enter your server address, port, " +
                           "slot name and password. The mod will connect to your multiworld " +
                           "server directly. This launcher cannot pre-fill the connection " +
                           "details for Refunct.",
            FontSize     = 11,
            Foreground   = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Guided setup steps ────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "GUIDED SETUP",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 0, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Refunct on Steam. Use the folder picker above if it was not detected.",
            "2. Install the Archipelago mod: click Install on the Play tab (the launcher " +
               "downloads the latest mod release and extracts it into Refunct's " +
               "Binaries\\Win64 folder), or do it by hand from the mod releases link below.",
            "3. Launch Refunct from this launcher. The mod loads automatically when the " +
               "game starts — no extra steps are needed to enable it.",
            "4. In-game, open the Archipelago connection menu (check the mod repo for the " +
               "exact key binding) and enter your Server, Port, Slot Name and Password.",
            "5. Confirm the mod shows a successful connection. Enjoy — checks are sent to " +
               "your multiworld as you press buttons and restore the platforms.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text         = step,
                FontSize     = 11,
                Foreground   = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin       = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "LINKS",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("refunct-tas-archipelago (GitHub) ↗", RepoUrl),
            ("Refunct Setup Guide (AP) ↗",         SetupGuideUrl),
            ("Archipelago Official ↗",             ArchipelagoSite),
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
                                          System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor              = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); }
                catch { /* ignore — browser may be unavailable */ }
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

    /// "v1.2.3" → "1.2.3" (leading 'v' + digit stripped); else trimmed.
    /// Returns null for blank/null input.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest GitHub release: version + download URL for the first
    /// .zip asset. Falls back to a pinned tag when the API is unreachable (with no
    /// download URL — the API is the reliable source for asset names).
    private async Task<(string Version, string? ZipUrl)> ResolveLatestReleaseAsync(
        CancellationToken ct)
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
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        return (version, url);
                }
                // No zip found but we have a version; report version, no download URL.
                return (version, null);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback below */ }

        return (FallbackVersion, null);
    }

    // ── Private helpers — Steam / Refunct detection ───────────────────────────

    /// The Refunct install dir to use: the override (if set + valid) wins, else
    /// the Steam-detected install. Null when nothing is found.
    private string? ResolveRefunctDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeRefunctDir(ov))
            return ov;

        try { return DetectSteamRefunctDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Refunct when it contains the shipping exe (in
    /// Binaries\Win64\, or at the root), or a "Binaries" sub-folder (the standard
    /// UE4 install layout).
    private static bool LooksLikeRefunctDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            // Standard UE4: shipping exe in Binaries\Win64\.
            if (File.Exists(Path.Combine(dir, "Binaries", "Win64", ShippingExeName)))
                return true;
            // Non-standard: shipped with exe at root (some packaging variants).
            if (File.Exists(Path.Combine(dir, ShippingExeName)))
                return true;
            if (File.Exists(Path.Combine(dir, "Refunct.exe")))
                return true;
            // Secondary: just the Binaries folder (game unpacked but exe not yet verified).
            if (Directory.Exists(Path.Combine(dir, "Binaries")))
                return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam Refunct install: read Steam root from the registry, gather
    /// all library roots from libraryfolders.vdf, and locate
    /// steamapps\common\Refunct via appmanifest_376030.acf.
    private static string? DetectSteamRefunctDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{RefunctSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common      = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeRefunctDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeRefunctDir(conventional)) return conventional;
                }
                catch { /* try next library */ }
            }
        }
        return null;
    }

    /// Candidate Steam install roots from registry (HKCU + HKLM variants +
    /// conventional path). Duplicates are harmless.
    private static IEnumerable<string> SteamRoots()
    {
        string? hkcu = ReadRegistryString(Registry.CurrentUser,
            @"Software\Valve\Steam", "SteamPath");
        if (!string.IsNullOrWhiteSpace(hkcu)) yield return NormalizeSteamPath(hkcu);

        string? hklm = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm)) yield return NormalizeSteamPath(hklm);

        string? hklm2 = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm2)) yield return NormalizeSteamPath(hklm2);

        string? progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

    /// Steam stores SteamPath with forward slashes; normalize for Windows Path APIs.
    private static string NormalizeSteamPath(string p)
        => p.Replace('/', '\\').TrimEnd('\\');

    /// All Steam library roots: the Steam root itself plus every "path" entry in
    /// steamapps\libraryfolders.vdf. Tolerant text scan.
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

    /// Pull every "path" "<value>" pair out of a libraryfolders.vdf body.
    private static IEnumerable<string> ExtractVdfPaths(string text)
    {
        const string key = "\"path\"";
        int i = 0;
        while ((i = text.IndexOf(key, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            i += key.Length;
            int open  = text.IndexOf('"', i);
            if (open < 0) yield break;
            int close = text.IndexOf('"', open + 1);
            if (close < 0) yield break;
            string raw = text.Substring(open + 1, close - open - 1);
            yield return raw.Replace("\\\\", "\\");
            i = close + 1;
        }
    }

    /// Read "installdir" from an appmanifest_*.acf. Returns null if absent.
    private static string? ReadAcfInstallDir(string acfPath)
    {
        try
        {
            string text = File.ReadAllText(acfPath);
            const string key = "\"installdir\"";
            int i = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            i += key.Length;
            int open  = text.IndexOf('"', i);
            if (open < 0) return null;
            int close = text.IndexOf('"', open + 1);
            if (close < 0) return null;
            return text.Substring(open + 1, close - open - 1).Replace("\\\\", "\\");
        }
        catch { return null; }
    }

    /// Safe registry string read; null on any failure.
    private static string? ReadRegistryString(RegistryKey hive, string subKey, string valueName)
    {
        try
        {
            using RegistryKey? k = hive.OpenSubKey(subKey);
            return k?.GetValue(valueName) as string;
        }
        catch { return null; }
    }

    // ── Private helpers — installed-mod detection ─────────────────────────────

    /// Returns the path to the first mod indicator found, or null when the mod is
    /// not detected. Looks in Binaries\Win64\ for:
    ///   1. Our own version stamp file (launcher-driven install).
    ///   2. "dwmapi.dll" — the conventional UE4 proxy-DLL injection entry point.
    ///   3. Any file whose name mentions "archipelago" (case-insensitive).
    private string? FindInstalledModIndicator()
    {
        try
        {
            string? game = ResolveRefunctDir();
            if (game == null) return null;

            // Check both Binaries\Win64\ and the game root.
            var searchDirs = new[]
            {
                Path.Combine(game, "Binaries", "Win64"),
                game,
            };

            foreach (string dir in searchDirs)
            {
                if (!Directory.Exists(dir)) continue;

                // 1. Our stamp file.
                string stamp = Path.Combine(dir, VersionFileName);
                if (File.Exists(stamp)) return stamp;

                // 2. Common UE4 proxy-DLL injection file.
                string proxy = Path.Combine(dir, "dwmapi.dll");
                if (File.Exists(proxy)) return proxy;

                // 3. Any file mentioning "archipelago".
                try
                {
                    foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
                    {
                        if (Path.GetFileName(f).IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                            return f;
                    }
                }
                catch { /* permission — skip */ }
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Resolve the shipping exe: checks Binaries\Win64\Refunct-Win64-Shipping.exe,
    /// then the game root, then a fuzzy scan of Binaries\Win64\ for *Refunct*.exe.
    private static string? ResolveShippingExe(string gameDir)
    {
        // Primary: canonical UE4 shipping exe location.
        string canonical = Path.Combine(gameDir, "Binaries", "Win64", ShippingExeName);
        if (File.Exists(canonical)) return canonical;

        // Secondary: exe at game root (unusual, but some packaging variants do this).
        string atRoot = Path.Combine(gameDir, ShippingExeName);
        if (File.Exists(atRoot)) return atRoot;

        string atRootSimple = Path.Combine(gameDir, "Refunct.exe");
        if (File.Exists(atRootSimple)) return atRootSimple;

        // Defensive: fuzzy scan of Binaries\Win64\ for *Refunct*.exe.
        string binWin64 = Path.Combine(gameDir, "Binaries", "Win64");
        try
        {
            foreach (string exe in Directory.EnumerateFiles(binWin64, "*.exe", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetFileNameWithoutExtension(exe).IndexOf("refunct", StringComparison.OrdinalIgnoreCase) >= 0)
                    return exe;
            }
        }
        catch { /* directory vanished or permission issue */ }

        return null;
    }

    /// Start Refunct: prefer the exe in the detected/override install; if not found
    /// but Steam is present, fall back to the steam:// URL.
    private void StartRefunct()
    {
        string? game = ResolveRefunctDir();
        string? exe  = game != null ? ResolveShippingExe(game) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = game!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Refunct.");

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
            catch { /* some processes don't expose Exited — non-fatal */ }
            return;
        }

        // Fall back to Steam if we at least know Steam is installed.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true; // best-effort; Steam owns the process, won't track exit
                return;
            }
            catch { /* fall through to the error below */ }
        }

        throw new FileNotFoundException(
            "Could not find Refunct-Win64-Shipping.exe. Open this game's Settings and " +
            "pick your Refunct install folder, or install Refunct via Steam.",
            ShippingExeName);
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the mod zip and extract its contents into the game's Binaries\Win64\
    /// folder (standard UE4 mod install location). Flattens a single top-level
    /// subfolder if the zip wraps its files in one.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string targetDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"refunct-ap-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((10, $"Downloading Refunct Archipelago mod {version}..."));
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
                        int pct = (int)(10 + 65 * downloaded / total);
                        progress.Report((pct, $"Downloading mod... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((78, "Extracting mod files into game folder..."));
            Directory.CreateDirectory(targetDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, targetDir, overwriteFiles: true);

            // Flatten a single top-level subfolder if the zip used one (defensive).
            string[] subdirs = Directory.GetDirectories(targetDir);
            if (subdirs.Length == 1 && !Directory.EnumerateFiles(targetDir, "*", SearchOption.TopDirectoryOnly).Any())
            {
                string sub = subdirs[0];
                foreach (string fileSrc in Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories))
                {
                    string rel     = Path.GetRelativePath(sub, fileSrc);
                    string fileDst = Path.Combine(targetDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(fileDst)!);
                    File.Move(fileSrc, fileDst, overwrite: true);
                }
                try { Directory.Delete(sub, recursive: true); } catch { }
            }

            // Write our version stamp so the launcher can show the installed version.
            string stampPath = Path.Combine(targetDir, VersionFileName);
            await File.WriteAllTextAsync(stampPath, version, ct);

            progress.Report((95, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // Keeps the install-dir override and an informational version stamp in a
    // separate JSON file so this plugin does not touch the shared SettingsStore.
    // BOM-less UTF-8, read-modify-write.

    private sealed class RefunctSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private RefunctSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SidecarPath))
            {
                string txt = File.ReadAllText(SidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<RefunctSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(RefunctSettings s)
    {
        try
        {
            Directory.CreateDirectory(SidecarDir);
            File.WriteAllText(SidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal — setting just won't persist this time */ }
    }

    private string? LoadOverrideDir()
    {
        string? p = LoadSettings().InstallOverride;
        return string.IsNullOrWhiteSpace(p) ? null : p;
    }

    private void SaveOverrideDir(string p)
    {
        var s = LoadSettings();
        s.InstallOverride = p;
        SaveSettings(s);
    }

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().ModVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private void WriteStampedVersion(string v)
    {
        var s = LoadSettings();
        s.ModVersion = v;
        SaveSettings(s);
    }
}
