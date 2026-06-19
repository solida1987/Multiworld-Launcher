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
using Microsoft.Win32;
using LauncherV2.Core;

// NOTE: WPF UI types are FULLY QUALIFIED throughout this file (System.Windows.*).
// The real launcher project sets <UseWindowsForms>true</UseWindowsForms> alongside
// WPF, so the bare names Button / TextBox / Color / Brushes / MessageBox /
// FontWeights / Orientation / Clipboard collide between System.Windows.Forms and
// System.Windows[.Controls/.Media] (CS0104). Qualifying every UI type avoids that.

namespace LauncherV2.Plugins.Pseudoregalia;

// ═══════════════════════════════════════════════════════════════════════════════
// PseudoregaliaPlugin — install / launch for "Pseudoregalia" (rittzler, 2023)
// played through the qwint/pseudoregalia-archipelago BepInEx mod, which is the
// in-game Archipelago client. This is a NATIVE "ConnectsItself" integration —
// the game itself speaks to the AP server (no emulator, no Lua bridge, no
// launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned
// Pseudoregalia (Steam appid 2365810) — a movement-focused 3D platformer /
// Metroidvania. Archipelago is a BepInEx 5 plugin (Unity game) added on top.
// The honest integration ceiling is "automate what is possible, guide the rest."
//
//   * THE AP WORLD game string is "Pseudoregalia" (verified against the
//     qwint/pseudoregalia-archipelago repository world file).
//
//   * THE MOD repo is qwint/pseudoregalia-archipelago on GitHub. Releases carry
//     a single zip asset containing the BepInEx/ folder; extract it into the
//     Steam game directory. The mod is a BepInEx 5 plugin.
//
//   * INSTALL: the user downloads the release zip from GitHub and extracts the
//     BepInEx/ folder into their Steam Pseudoregalia directory. If BepInEx itself
//     is not yet installed (it is NOT bundled — the game is Unity, BepInEx must
//     be installed separately for Unity games per the BepInEx docs), the plugin
//     guides the user to https://github.com/BepInEx/BepInEx/releases/ to install
//     BepInEx 5 first, then extracts the AP mod into the game folder. This
//     plugin downloads the release zip and extracts BepInEx/ into the game dir,
//     but cannot install BepInEx itself (it is a separate prerequisite).
//
//   * CONNECTION is FULLY IN-GAME. The mod adds an Archipelago connection UI
//     inside the game. There is no command-line / config file that can be
//     pre-written. ConnectsItself = true; the launcher must NOT hold its own
//     ApClient on this slot. SupportsStandalone = true.
//
//   * DETECTION: mod is present when BepInEx\plugins\ contains any DLL matching
//     "*pseudoregalia*" (case-insensitive) in a detected/override Steam install.
//
// ── BUILD NOTE ─────────────────────────────────────────────────────────────────
// UseWindowsForms=true → all WPF UI types spelled with full System.Windows.*
// namespaces to avoid CS0104. No file-level `using X = System.Windows...;`
// aliases (CS1537 — GlobalUsings already has the root aliases).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class PseudoregaliaPlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const string MOD_OWNER = "qwint";
    private const string MOD_REPO  = "pseudoregalia-archipelago";

    private static readonly string ModRepoUrl          = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private static readonly string ModReleasesPageUrl  = $"{ModRepoUrl}/releases";
    private static readonly string GhReleasesLatestUrl = $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private static readonly string GhReleasesUrl       = $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string BepInExSite    = "https://github.com/BepInEx/BepInEx/releases";
    private const string SetupGuideUrl  = "https://archipelago.gg/tutorial/Pseudoregalia/setup/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Pseudoregalia appid 2365810.
    private const string SteamAppId = "2365810";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    private const string SteamCommonFolderName = "Pseudoregalia";
    private const string GameExeName           = "Pseudoregalia.exe";

    /// Pinned fallback version when the GitHub API is unreachable.
    private const string FallbackVersion = "1.0.0";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "pseudoregalia";
    public string DisplayName => "Pseudoregalia";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against qwint/pseudoregalia-archipelago.
    public string ApWorldName => "Pseudoregalia";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "pseudoregalia.png");

    /// Dark purple / violet to match Pseudoregalia's dreamlike surreal aesthetic.
    public string ThemeAccentColor => "#6B3FA0";

    public string[] GameBadges => new[] { "Steam · needs mod" };

    public string Description =>
        "Pseudoregalia, rittzler's movement-focused 3D platformer and Metroidvania, " +
        "played through the qwint/pseudoregalia-archipelago BepInEx mod — an in-game " +
        "Archipelago client that lets the game speak to the multiworld itself. Abilities " +
        "and key items are shuffled across the randomizer, rewarding precise movement " +
        "and exploration. You bring your own copy of Pseudoregalia (owned on Steam), " +
        "and the Archipelago BepInEx mod is added on top. The launcher can detect your " +
        "Steam install and stage the mod files; you connect to your Archipelago server " +
        "from the in-game connection menu.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means an AP mod DLL is present under BepInEx\plugins in the
    /// detected/override Steam install. We do NOT gate on our own stamp — the
    /// user may have installed the zip by hand, which we honor.
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads and sidecar bookkeeping. The actual mod
    /// is extracted INTO the Pseudoregalia Steam install, not here. Exposed as
    /// GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Pseudoregalia");

    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "pseudoregalia_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The mod reports checks/items/goal to the AP server directly — the launcher
    // relays nothing. These exist only for interface compatibility.
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
            InstalledVersion = FindInstalledModDll() != null
                ? (ReadStampedVersion() ?? "installed")
                : null;
        }
        catch
        {
            InstalledVersion = null;
        }

        try
        {
            var (version, _) = await ResolveLatestModAsync(ct);
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
        // 0. We need a Pseudoregalia install to drop the mod into.
        progress.Report((2, "Locating your Pseudoregalia installation..."));
        string? gameDir = ResolvePseudoregaliaDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a Pseudoregalia installation. Open this game's Settings " +
                "and pick your Pseudoregalia folder (the one containing Pseudoregalia.exe), " +
                "or install Pseudoregalia via Steam first. The Archipelago mod is added " +
                "on top of your own copy of the game.");

        // 1. Resolve the latest mod release.
        progress.Report((6, "Checking the latest Archipelago mod release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the Archipelago Pseudoregalia mod download on GitHub. " +
                "Check your internet connection, or download the mod zip manually from " +
                ModReleasesPageUrl + " and extract the BepInEx/ folder into your " +
                "Pseudoregalia game folder. See Settings for the guided steps.");

        // 2. Download + extract the mod zip into the game folder.
        //    HONEST scope: this drops the BepInEx/plugins content into the game dir.
        //    BepInEx itself must already be installed for Unity (BepInEx 5); the
        //    post-install message and settings panel state this clearly.
        await DownloadAndExtractModAsync(zipUrl, version, gameDir, progress, ct);

        // 3. Stamp the version (informational only).
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool bepInExPresent = Directory.Exists(Path.Combine(gameDir, "BepInEx", "core"));
        progress.Report((100,
            $"Staged the Archipelago mod {version} into your Pseudoregalia folder. " +
            (bepInExPresent
                ? "BepInEx appears to be installed. "
                : "IMPORTANT: BepInEx 5 must be installed separately into your Pseudoregalia " +
                  "folder before the mod will load (see the BepInEx link in Settings). ") +
            "To play: launch the game, then use the in-game Archipelago connection menu " +
            "to enter your server URL, port, slot name, and optional password."));
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
        // Connection is entered IN-GAME via the mod's Archipelago connection menu.
        // No command-line / config-file prefill mechanism exists. ConnectsItself=true:
        // the launcher must NOT hold its own ApClient on this slot.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartPseudoregalia();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) Pseudoregalia runs perfectly well.
    public bool SupportsStandalone => true;

    /// The mod owns the slot connection — the launcher must not connect.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartPseudoregalia();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // No plaintext AP password is ever written to disk by this plugin
        // (connection is entered in-game), so there is nothing to scrub.
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Existing-install validation ───────────────────────────────────────────

    /// A valid Pseudoregalia folder contains Pseudoregalia.exe. Returns null when
    /// acceptable, else a short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Pseudoregalia install folder.";

        if (LooksPseudoregaliaDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksPseudoregaliaDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Pseudoregalia installation. Pick the folder " +
               "that contains Pseudoregalia.exe. For Steam this is usually " +
               @"...\steamapps\common\Pseudoregalia.";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0xE8));
        var accent  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9B, 0x6B, 0xD4));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));

        var panel = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20)
        };

        string? gameDir     = ResolvePseudoregaliaDir();
        string? overrideDir = LoadOverrideDir();
        string? modDll      = FindInstalledModDll();
        bool    bepInExOk   = gameDir != null && Directory.Exists(Path.Combine(gameDir, "BepInEx", "core"));

        // ── Header ────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Pseudoregalia is your own game (Steam) with the Archipelago BepInEx mod " +
                   "added on top. The launcher can detect your Steam install and stage the " +
                   "mod files, but BepInEx 5 must be installed separately for Unity games " +
                   "before the mod will load (see the BepInEx link below). You connect to " +
                   "your Archipelago server from the in-game connection menu — there is no " +
                   "connection file to pre-fill.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: Game install ─────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "PSEUDOREGALIA INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Pseudoregalia not detected. Pick your install folder below, or install " +
              "Pseudoregalia via Steam first (appid 2365810).";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = bepInExOk
                    ? "BepInEx found (BepInEx\\core present)."
                    : "BepInEx not found yet — install BepInEx 5 from the link below before " +
                      "installing the mod.",
            FontSize = 11, Foreground = bepInExOk ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                    ? "Archipelago mod found: " + modDll
                    : "Archipelago mod not found in BepInEx\\plugins yet. Use Install on the " +
                      "Play tab, or extract the release zip from the mod repo into your " +
                      "Pseudoregalia folder manually.",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // ── Folder picker row ─────────────────────────────────────────────
        var dirRow = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4)
        };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? gameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x0C, 0x1A)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x1E, 0x44)),
            ToolTip     = "Your Pseudoregalia install folder (the one containing Pseudoregalia.exe). " +
                          "Detected from Steam automatically; set it here to override.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x10, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4A, 0x2A, 0x70)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Pseudoregalia install folder (contains Pseudoregalia.exe)",
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
                    System.Windows.MessageBox.Show(bad, "Not a Pseudoregalia folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                if (!LooksPseudoregaliaDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksPseudoregaliaDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 2365810). Use this picker " +
                   "for a non-standard Steam library or a manual install.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection ───────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in-game)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "After the mod is installed and the game launched, use the in-game " +
                   "Archipelago connection menu to enter your server URL, port, slot name, " +
                   "and optional password. The launcher cannot pre-fill connection details " +
                   "(ConnectsItself = true — the mod handles the slot directly).",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Pseudoregalia on Steam (appid 2365810). Install it if you have not. " +
                "Use \"Select folder...\" above if it was not auto-detected.",
            "2. Install BepInEx 5 into your Pseudoregalia folder: download the BepInEx 5 " +
                "release zip for Unity (x64) from the BepInEx GitHub releases page, and " +
                "extract it into your Pseudoregalia game directory so that " +
                "Pseudoregalia\\BepInEx\\core\\ exists. Run the game once to let BepInEx " +
                "initialise, then close it.",
            "3. Install the Archipelago mod: use Install on the Play tab (the launcher " +
                "downloads the release zip from qwint/pseudoregalia-archipelago and extracts " +
                "the BepInEx/ folder into your Pseudoregalia directory). Or download and " +
                "extract it manually from the mod releases page.",
            "4. Launch Pseudoregalia from this launcher (or normally). BepInEx loads the " +
                "Archipelago plugin automatically.",
            "5. In-game, use the Archipelago connection menu to enter your server URL, port, " +
                "slot name, and optional password. Connect to start your multiworld session.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LINKS", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("Pseudoregalia Archipelago Mod (releases) ↗", ModReleasesPageUrl),
            ("BepInEx 5 (mod loader for Unity) ↗",         BepInExSite),
            ("Pseudoregalia Setup Guide ↗",                 SetupGuideUrl),
            ("Pseudoregalia on Steam ↗",                    $"https://store.steampowered.com/app/{SteamAppId}"),
            ("Archipelago Official ↗",                      ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new System.Windows.Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0),
                FontSize = 12,
                Margin = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground = accent,
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

    /// "v1.2.3" → "1.2.3" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + the zip download URL from the
    /// GitHub API. Prefers an asset whose name contains "pseudoregalia" and ends
    /// with ".zip"; falls back to any .zip; falls back to the pinned version when
    /// the API is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestModAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GhReleasesLatestUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? preferred = null;
                string? anyZip    = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    anyZip ??= url;
                    if (preferred == null &&
                        (lower.Contains("pseudoregalia") || lower.Contains("archipelago")))
                        preferred = url;
                }
                string? zip = preferred ?? anyZip;
                if (zip != null)
                    return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable → pinned fallback with no URL */ }

        // Pinned fallback: no URL because the asset name pattern is not known at
        // compile time. The UI will show the releases page link so the user can
        // download manually.
        return (FallbackVersion, null);
    }

    // ── Private helpers — Steam / Pseudoregalia detection ────────────────────

    /// The install dir to use: the override (if set and valid) wins, else the
    /// Steam-detected install. Null when nothing is found.
    private string? ResolvePseudoregaliaDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksPseudoregaliaDir(ov))
            return ov;

        try { return DetectSteamPseudoregaliaDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Pseudoregalia if it contains Pseudoregalia.exe.
    private static bool LooksPseudoregaliaDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, GameExeName));
        }
        catch { return false; }
    }

    /// Detect the Steam Pseudoregalia install via the registry + libraryfolders.vdf.
    private static string? DetectSteamPseudoregaliaDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{SteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksPseudoregaliaDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksPseudoregaliaDir(conventional)) return conventional;
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    /// Candidate Steam install roots from the registry.
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
            string raw = text.Substring(open + 1, close - open - 1);
            yield return raw.Replace("\\\\", "\\");
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

    // ── Private helpers — installed-mod detection ─────────────────────────────

    /// Find the AP mod DLL under the detected/override install's BepInEx\plugins
    /// tree. Accepts any *.dll whose name contains "pseudoregalia" (case-insensitive).
    /// Returns the DLL path or null.
    private string? FindInstalledModDll()
    {
        try
        {
            string? game = ResolvePseudoregaliaDir();
            if (game == null) return null;

            string pluginsDir = Path.Combine(game, "BepInEx", "plugins");
            if (!Directory.Exists(pluginsDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(
                pluginsDir, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(dll);
                if (name.IndexOf("pseudoregalia", StringComparison.OrdinalIgnoreCase) >= 0)
                    return dll;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartPseudoregalia()
    {
        string? game = ResolvePseudoregaliaDir();
        string? exe  = game != null ? Path.Combine(game, GameExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = game!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Pseudoregalia.");

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
            catch { /* some processes don't expose Exited — non-fatal */ }
            return;
        }

        // Fall back to Steam if we can find a Steam installation.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true;
                return;
            }
            catch { /* fall through to the error below */ }
        }

        throw new FileNotFoundException(
            "Could not find Pseudoregalia.exe. Open this game's Settings and pick your " +
            "Pseudoregalia install folder, or install Pseudoregalia via Steam.",
            GameExeName);
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the release zip and extract the BepInEx/ folder into the
    /// Pseudoregalia game directory. The zip from qwint/pseudoregalia-archipelago
    /// contains a BepInEx/ tree; we merge it into the game directory so the plugin
    /// DLL ends up under BepInEx\plugins\. BepInEx itself is a separate prerequisite
    /// that the user installs first.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string gameDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"pseudoregalia-ap-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"pseudoregalia-ap-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading Archipelago mod {version}..."));
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
                        int pct = (int)(10 + 55 * downloaded / total);
                        progress.Report((pct, $"Downloading... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((70, "Extracting mod package..."));
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            progress.Report((85, "Installing mod into Pseudoregalia folder..."));

            // Resolve the merge root: the extracted tree should have a BepInEx/
            // folder at its root (or wrapped in one top-level subfolder).
            string mergeRoot = tempExtract;
            if (!Directory.Exists(Path.Combine(mergeRoot, "BepInEx")))
            {
                string[] subdirs = Directory.GetDirectories(mergeRoot);
                string[] files   = Directory.GetFiles(mergeRoot);
                if (files.Length == 0 && subdirs.Length == 1 &&
                    Directory.Exists(Path.Combine(subdirs[0], "BepInEx")))
                    mergeRoot = subdirs[0];
            }

            MergeDirectory(mergeRoot, gameDir);
            progress.Report((95, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip))    File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    /// Recursively copy everything under src into dst (overwrite, never delete).
    private static void MergeDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, dir);
            Directory.CreateDirectory(Path.Combine(dst, rel));
        }
        foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string rel    = Path.GetRelativePath(src, file);
            string target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class PseudoregaliaSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private PseudoregaliaSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<PseudoregaliaSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(PseudoregaliaSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
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
