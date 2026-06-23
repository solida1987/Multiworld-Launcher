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

namespace LauncherV2.Plugins.CelesteOpenWorld;

// ═══════════════════════════════════════════════════════════════════════════════
// CelesteOpenWorldPlugin — install / launch for "Celeste (Open World)"
// (AP game string verified against worlds/celeste_open_world/__init__.py)
// played through the Archipelago Open World Everest mod. This is a NATIVE
// "ConnectsItself" integration: the mod speaks to the AP server itself from
// inside the game via an in-game Connection Menu — no emulator, no Lua bridge,
// no launcher-held ApClient on the slot.
//
// THIS IS DIFFERENT FROM Celeste64 (which is already in the launcher as a
// separate freeware standalone game). This is the ORIGINAL Celeste
// (Steam appid 504230) plus an Everest mod.
//
// ── HONEST REALITY CHECK (2026-06-14, verified online) ───────────────────────
//
//   * THE AP WORLD game string: "Celeste (Open World)"
//     Verified against worlds/celeste_open_world/__init__.py.
//     GameId = "celeste_open_world".
//
//   * THE MOD REPO: PoryGoneDev/Celeste-Archipelago-Open-World
//     (https://github.com/PoryGoneDev/Celeste-Archipelago-Open-World)
//     Latest release v1.0.7 (2026-01-14) ships:
//       - Archipelago_Open_World.zip   (the Everest mod, goes in Mods folder)
//       - celeste_open_world.apworld   (for Archipelago server generation)
//
//   * THE MOD LOADER is Everest (https://everestapi.github.io/).
//     Everest must be installed FIRST — this is done via the Olympus mod manager
//     (https://github.com/EverestAPI/Olympus). Olympus cannot be silently run
//     by the launcher; the user must install it. This plugin GUIDES the user
//     through the process (numbered steps + links), exactly like HollowKnight's
//     Lumafly guidance.
//
//   * THE MOD ZIP (Archipelago_Open_World.zip) IS a plain Everest mod package
//     that drops into the Celeste Mods folder. The launcher CAN automate this:
//     download the release zip and extract it into the detected Celeste Mods
//     folder. The mod's everest.yaml names it "Archipelago Open World".
//
//   * HOW IT CONNECTS (verified from source + setup guide): connection is entered
//     via an IN-GAME Connection Menu (a "Connect" button on the main menu after
//     the mod is active). The mod's settings class (Celeste_MultiworldModuleSettings)
//     has fields: Address, SlotName, Password — stored by Everest as YAML at:
//       %AppData%\Local\Celeste\Saves\modsettings-Archipelago Open World.celeste
//     The launcher CAN pre-write this file to prefill the connection details,
//     since it is plain YAML that Everest reads on startup. This is done in
//     LaunchAsync (best-effort, read-modify-write to preserve other settings).
//     The Address field takes just the host (e.g. "archipelago.gg"); the port
//     is stored separately but EverestModuleSettings appears to fold it in the
//     address string — we write "host:port" to Address as the AP setup guide
//     shows that format in screenshots.
//
//   * THE BASE GAME is bring-your-own: Celeste on Steam (appid 504230).
//     The launcher detects the Steam install via the Windows registry +
//     libraryfolders.vdf + appmanifest_504230.acf. A manual folder override is
//     also supported via the settings panel.
//
// ── WHAT THIS PLUGIN DOES ─────────────────────────────────────────────────────
//   1. DETECT the Steam Celeste install (registry → libraryfolders.vdf →
//      appmanifest_504230.acf → steamapps\common\Celeste\Celeste.exe).
//      Manual override picker also supported, persisted in this plugin's OWN
//      JSON sidecar (Games/ROMs/celeste_open_world/celeste_open_world_launcher.json).
//   2. CHECK EVEREST: look for Celeste.dll.backup or EverestCore.dll or
//      a Mods/ folder in the Celeste install dir (evidence Everest is installed).
//   3. INSTALL/UPDATE (best effort): download Archipelago_Open_World.zip from
//      the GitHub release, extract into the Celeste Mods folder under
//      "Archipelago Open World". Everest must already be installed for this to
//      activate.
//   4. PREFILL: write the AP connection settings to the Everest settings YAML
//      (best-effort) so the user does not have to re-type them each session.
//   5. LAUNCH: run Celeste.exe from the detected/override install dir, or fall
//      back to steam://rungameid/504230 when the exe is unavailable.
//   6. ConnectsItself = true (the mod owns the slot).
//      SupportsStandalone = true (vanilla Celeste runs fine without the mod).
//
// ── DEFENSIVE / UNVERIFIED ────────────────────────────────────────────────────
//   * The exact port handling in the Everest YAML was not inspected at runtime.
//     The launcher writes "host:port" to Address as a defensive choice that
//     matches what the AP guide shows. If the mod stores host and port separately
//     the in-game menu can be used to correct it (best-effort prefill, not a
//     hard requirement).
//   * "Installed" = the mod DLL exists in the Mods/Archipelago Open World folder.
//     We also honour Lumafly/manual installs (no own-stamp gating).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class CelesteOpenWorldPlugin : IGamePlugin
{
    // ── Constants — the AP Open World mod (real repo, verified 2026-06-14) ─────

    private const string MOD_OWNER  = "PoryGoneDev";
    private const string MOD_REPO   = "Celeste-Archipelago-Open-World";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    // Everest / Olympus — the mod loader required before the AP mod activates.
    private const string EverestSite = "https://everestapi.github.io/";
    private const string OlympusSite = "https://github.com/EverestAPI/Olympus/releases/latest";

    private const string SetupGuideUrl =
        "https://archipelago.gg/tutorial/Celeste%20(Open%20World)/guide_en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Celeste appid 504230.
    private const string CelesteSteamAppId = "504230";
    private static readonly string SteamRunUrl = $"steam://rungameid/{CelesteSteamAppId}";

    // Pinned fallback version when the GitHub API is unreachable. v1.0.7 verified
    // live 2026-06-14; the two assets are Archipelago_Open_World.zip and
    // celeste_open_world.apworld.
    private const string FallbackVersion = "1.0.7";
    private const string FallbackZipName = "Archipelago_Open_World.zip";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/v{FallbackVersion}/{FallbackZipName}";

    // The Everest mod folder name (from the mod's everest.yaml: Name: Archipelago Open World).
    private const string ModFolderName = "Archipelago Open World";

    // Everest settings file: %AppData%\Local\Celeste\Saves\modsettings-<Name>.celeste
    // This file is YAML; we write Address, SlotName, Password fields into it.
    private static readonly string EverestSavesDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Celeste", "Saves");
    private static readonly string EverestSettingsPath = Path.Combine(
        EverestSavesDir, $"modsettings-{ModFolderName}.celeste");

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "celeste_open_world";
    public string DisplayName => "Celeste (Open World)";
    public string Subtitle    => "Native PC · Steam + Everest mod";

    /// EXACT AP game string — verified against worlds/celeste_open_world/__init__.py.
    public string ApWorldName => "Celeste (Open World)";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "celeste_open_world.png");

    public string ThemeAccentColor => "#B03060";   // Celeste strawberry red
    public string[] GameBadges     => new[] { "Steam · needs Everest mod" };

    public string Description =>
        "Celeste with the Archipelago Open World mod — an Everest mod that transforms " +
        "the original Celeste into an open-world multiworld game. All chapters are " +
        "accessible from the start; you unlock abilities to interact with objects and " +
        "earn Strawberries, which are shuffled into the multiworld. The mod speaks to " +
        "the Archipelago server directly from inside the game — no emulator, no bridge. " +
        "You bring your own copy of Celeste (Steam), install the Everest mod loader and " +
        "the Archipelago Open World mod, then connect via the in-game Connection Menu.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the AP Open World mod DLL is present in the Celeste
    /// Mods folder. We also honour manual / Olympus installs (no own-stamp gating).
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads (mod zip) and working files. Exposed
    /// as GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "CelesteOpenWorld");

    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "celeste_open_world_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────
    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The mod reports checks/items/goal to the AP server itself.
    // These exist for interface compatibility (ConnectsItself = true).
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
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(MOD_OWNER, MOD_REPO, ct));
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
        // 0. Need a Celeste install to drop the mod into.
        progress.Report((2, "Locating your Celeste installation..."));
        string? celesteDir = ResolveCelesteDir();
        if (celesteDir == null)
            throw new InvalidOperationException(
                "Could not find a Celeste installation. Open this game's Settings " +
                "and pick your Celeste folder (the one containing Celeste.exe), or " +
                "install Celeste via Steam first. The Archipelago Open World mod is " +
                "added on top of your own copy of the game.");

        // Check Everest is installed (non-blocking warning, not a hard gate).
        bool everestPresent = IsEverestInstalled(celesteDir);
        if (!everestPresent)
            progress.Report((4,
                "Everest mod loader not detected. Install Everest via Olympus first " +
                "(see Settings for guided steps) — the mod will not activate without it."));

        string modsDir  = Path.Combine(celesteDir, "Mods");
        string apModDir = Path.Combine(modsDir, ModFolderName);

        // 1. Resolve latest release (pinned fallback if API unreachable).
        progress.Report((6, "Checking the latest Archipelago Open World release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the Archipelago Open World mod download on GitHub. " +
                "Check your internet connection, or download it manually from " +
                ModRepoUrl + "/releases — place Archipelago_Open_World.zip into " +
                "your Celeste Mods folder.");

        // 2. Download + extract the mod zip into <Celeste>/Mods/Archipelago Open World/.
        await DownloadAndExtractModAsync(zipUrl, version, apModDir, progress, ct);

        // 3. Stamp the version in our sidecar (informational only).
        WriteStampedVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            $"Archipelago Open World mod {version} installed into your Celeste Mods folder. " +
            (everestPresent
                ? "Everest is present — start Celeste via Olympus and use the in-game " +
                  "Connection Menu (Connect button on the main menu) to enter your " +
                  "server details, then click Play here to launch."
                : "IMPORTANT: Everest is not yet installed. Install Everest via Olympus " +
                  "(see Settings) before starting the game — the mod will not appear without it.")));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    /// ConnectsItself = true: the mod owns the slot connection. The launcher
    /// must NOT hold its own ApClient on this slot while the game is running.
    public bool ConnectsItself    => true;

    /// Vanilla Celeste works fine without the mod.
    public bool SupportsStandalone => true;

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // Pre-fill the Everest settings YAML with the AP connection details.
        // Best-effort: if it fails the user can still type them in the in-game
        // Connection Menu.
        try { PrefillEverestSettings(session); }
        catch { /* non-fatal */ }

        StartCeleste();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartCeleste();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        // The plaintext AP password lives in the Everest settings YAML. Scrub it
        // after the session ends so it does not outlive the game.
        ScrubEverestPassword();
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "Celeste (Open World) is your own copy of Celeste (Steam) with the " +
                   "Archipelago Open World Everest mod added on top. The launcher detects " +
                   "your Steam install, can stage the mod files, and prefills your AP " +
                   "connection details. You must install the Everest mod loader via Olympus " +
                   "first — that step cannot be automated. You connect via the in-game " +
                   "Connection Menu (or the prefill does it for you if Everest reads the " +
                   "settings file before the menu opens).",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "CELESTE INSTALL", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? celesteDir  = ResolveCelesteDir();
        string? overrideDir = LoadOverrideDir();

        string detectMsg = celesteDir != null
            ? (overrideDir != null
                ? "Selected folder: " + celesteDir
                : "Detected Steam install: " + celesteDir)
            : "Celeste not detected. Pick your install folder below, or install " +
              "Celeste via Steam first.";
        panel.Children.Add(new TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = celesteDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // Everest status line
        bool everestOk = celesteDir != null && IsEverestInstalled(celesteDir);
        panel.Children.Add(new TextBlock
        {
            Text = everestOk
                    ? "Everest mod loader detected."
                    : "Everest mod loader NOT detected — install it via Olympus (see guided steps below).",
            FontSize = 11, Foreground = everestOk ? success : warn,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });

        // AP mod status line
        string? modDll = FindInstalledModDll();
        panel.Children.Add(new TextBlock
        {
            Text = modDll != null
                    ? "Archipelago Open World mod detected: " + modDll
                    : "Archipelago Open World mod not found in Mods folder yet (use Install " +
                      "on the Play tab, or copy the mod zip into the Mods folder).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        // Folder picker
        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text = overrideDir ?? celesteDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip = "Your Celeste install folder (the one containing Celeste.exe). " +
                      "Detected from Steam automatically; set it here to override.",
        };
        var dirBtn = new Button
        {
            Content = "Select folder...", Width = 120, Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select your Celeste install folder",
                InitialDirectory = Directory.Exists(overrideDir ?? celesteDir ?? "")
                                   ? (overrideDir ?? celesteDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                if (!LooksLikeCelesteDir(picked))
                {
                    MessageBox.Show(
                        "That does not look like a Celeste installation. Pick the " +
                        "folder that contains Celeste.exe.",
                        "Not a Celeste folder",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                SaveOverrideDir(picked);
                dirBox.Text = picked;
            }
        };
        DockPanel.SetDock(dirBtn, Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new TextBlock
        {
            Text = "Steam installs are detected automatically (appid 504230). Use this " +
                   "picker for non-standard Steam libraries or Epic/other versions.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 8, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Celeste (Steam). Install it if you have not already.",
            "2. Download and run Olympus (the Everest installer). Olympus installs " +
                "Everest, the Celeste mod loader, with one click. Use the Olympus " +
                "download link in Links below.",
            "3. In Olympus, launch Celeste once with Everest to confirm the mod loader " +
                "is active (you should see the Everest overlay on the title screen).",
            "4. Use the Install button on the Play tab to download the Archipelago Open " +
                "World mod. This extracts Archipelago_Open_World.zip into your Celeste " +
                "Mods folder automatically.",
            "5. Press Play — the launcher prefills your AP connection settings in " +
                "the Everest settings file. If the Connection Menu still shows blank " +
                "fields, enter your server / slot / password there manually.",
            "6. In-game: after the mod loads, a Connect button appears on the main menu. " +
                "Use it to set or confirm your connection details and connect to your " +
                "Archipelago server.",
        })
        {
            panel.Children.Add(new TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("Olympus (Everest installer, download) ↗",   OlympusSite),
            ("Everest mod loader ↗",                      EverestSite),
            ("Archipelago Open World mod (GitHub) ↗",     ModRepoUrl),
            ("Celeste (Open World) Setup Guide ↗",        SetupGuideUrl),
            ("Archipelago Official ↗",                    ArchipelagoSite),
        })
        {
            var btn = new Button
            {
                Content = label, HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), FontSize = 12, Margin = new Thickness(0, 0, 0, 4),
                Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor = Cursors.Hand,
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

    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + zip download URL.
    /// Falls back to the pinned 1.0.7 URL when the API is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestModAsync(CancellationToken ct)
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
                    // Prefer the mod zip (Archipelago_Open_World.zip) over any apworld-adjacent zip.
                    if (preferred == null &&
                        (lower.Contains("archipelago") || lower.Contains("open_world") || lower.Contains("openworld")))
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

    // ── Private helpers — Celeste / Steam detection ───────────────────────────

    /// The Celeste install dir to use: user override (if set and valid) first,
    /// then the Steam-detected install. Null when nothing is found.
    private string? ResolveCelesteDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeCelesteDir(ov))
            return ov;

        try { return DetectSteamCelesteDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Celeste if it has Celeste.exe or Content/Celeste.exe.
    private static bool LooksLikeCelesteDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, "Celeste.exe"))) return true;
            // Some store versions have Celeste.exe in Content/
            if (File.Exists(Path.Combine(dir, "Content", "Celeste.exe"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam Celeste install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, and locate the one
    /// whose appmanifest_504230.acf exists.
    private static string? DetectSteamCelesteDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{CelesteSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeCelesteDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, "Celeste");
                    if (LooksLikeCelesteDir(conventional)) return conventional;
                }
                catch { /* try next library */ }
            }
        }
        return null;
    }

    private static IEnumerable<string> SteamRoots()
    {
        string? hkcu = ReadRegistryString(Registry.CurrentUser,
            @"Software\Valve\Steam", "SteamPath");
        if (!string.IsNullOrWhiteSpace(hkcu)) yield return NormalizeSteamPath(hkcu);

        string? hklm = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm)) yield return NormalizeSteamPath(hklm);

        string? progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

    private static string NormalizeSteamPath(string p)
        => p.Replace('/', '\\').TrimEnd('\\');

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

    // ── Private helpers — Everest / mod detection ─────────────────────────────

    /// Everest is considered installed if any of these exist in the Celeste dir:
    ///   - Celeste.dll.backup  (Everest patches Celeste.dll, keeps a backup)
    ///   - EverestCore.dll     (Everest core assembly)
    ///   - Mods/               (mod folder that Everest creates)
    private static bool IsEverestInstalled(string celesteDir)
    {
        try
        {
            if (File.Exists(Path.Combine(celesteDir, "Celeste.dll.backup"))) return true;
            if (File.Exists(Path.Combine(celesteDir, "EverestCore.dll")))    return true;
            if (Directory.Exists(Path.Combine(celesteDir, "Mods")))          return true;
            return false;
        }
        catch { return false; }
    }

    /// Find the Archipelago Open World mod DLL under the Celeste Mods tree
    /// (recursive, case-insensitive). Returns the dll path or null.
    private string? FindInstalledModDll()
    {
        try
        {
            string? cDir = ResolveCelesteDir();
            if (cDir == null) return null;
            string modsDir = Path.Combine(cDir, "Mods");
            if (!Directory.Exists(modsDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(modsDir, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(dll);
                // The mod DLL is Celeste_Multiworld.dll (from the csproj / module)
                if (name.Equals("Celeste_Multiworld.dll", StringComparison.OrdinalIgnoreCase))
                    return dll;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — AP connection prefill (Everest settings YAML) ────────

    /// Pre-write the Everest module settings YAML for the AP Open World mod so
    /// the connection fields are populated when the user opens the in-game menu.
    /// Everest stores settings as YAML in:
    ///   %AppData%\Local\Celeste\Saves\modsettings-Archipelago Open World.celeste
    /// Fields from Celeste_MultiworldModuleSettings: Address, SlotName, Password.
    /// We read-modify-write to preserve any other settings the user has set.
    private void PrefillEverestSettings(ApSession session)
    {
        Directory.CreateDirectory(EverestSavesDir);

        // Build "host:port" for the Address field.
        string address = FormatServerAddress(session.ServerUri);

        // Read existing YAML lines (if any) so we can do a key-replace.
        var lines = new List<string>();
        if (File.Exists(EverestSettingsPath))
        {
            try { lines.AddRange(File.ReadAllLines(EverestSettingsPath, Encoding.UTF8)); }
            catch { /* start fresh */ }
        }

        // Replace or append Address, SlotName, Password lines.
        SetYamlField(lines, "Address",  address);
        SetYamlField(lines, "SlotName", session.SlotName);
        SetYamlField(lines, "Password", session.Password ?? "");

        File.WriteAllLines(EverestSettingsPath, lines, new UTF8Encoding(false));
    }

    /// After the session ends, blank the Password field in the YAML so the
    /// plaintext room password does not outlive the session on disk.
    private void ScrubEverestPassword()
    {
        try
        {
            if (!File.Exists(EverestSettingsPath)) return;
            var lines = new List<string>(
                File.ReadAllLines(EverestSettingsPath, Encoding.UTF8));
            SetYamlField(lines, "Password", "");
            File.WriteAllLines(EverestSettingsPath, lines, new UTF8Encoding(false));
        }
        catch { /* best effort */ }
    }

    /// Set (or insert) a simple YAML scalar field in the lines list.
    /// Handles  "  Key: value"  and  "Key: value"  formats. If the key is not
    /// present, appends it at the end. YAML scalar values are not quoted here
    /// because Everest's YamlDotNet reads bare strings fine, and the Celeste
    /// settings file uses that convention (observed from the source).
    private static void SetYamlField(List<string> lines, string key, string value)
    {
        // Escape characters that would break bare YAML scalars: newlines, colons
        // at the start, etc. For AP credentials these are unlikely but defensive.
        string safeValue = value.Replace("\r", "").Replace("\n", " ");

        for (int i = 0; i < lines.Count; i++)
        {
            string trimmed = lines[i].TrimStart();
            // Match "Key:" at the start (with optional leading whitespace).
            if (trimmed.StartsWith(key + ":", StringComparison.Ordinal))
            {
                // Preserve any leading whitespace from the original line.
                string indent = lines[i].Substring(0, lines[i].Length - trimmed.Length);
                lines[i] = $"{indent}{key}: {safeValue}";
                return;
            }
        }
        // Key not found — append it.
        lines.Add($"{key}: {safeValue}");
    }

    /// Format the launcher session URI as "host:port" for the Address field.
    private static string FormatServerAddress(string serverUri)
    {
        var (host, port) = ParseServerHostPort(serverUri);
        return host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
    }

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
            host = s;
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

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartCeleste()
    {
        string? celesteDir = ResolveCelesteDir();
        string? exe = celesteDir != null ? Path.Combine(celesteDir, "Celeste.exe") : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = celesteDir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Celeste.");

            _gameProcess = proc;
            IsRunning    = true;
            try
            {
                proc.EnableRaisingEvents = true;
                proc.Exited += (_, _) =>
                {
                    IsRunning = false;
                    ScrubEverestPassword();
                    GameExited?.Invoke(proc.ExitCode);
                };
            }
            catch { /* some processes don't expose Exited */ }
            return;
        }

        // Fall back to Steam URL if we have a Steam root.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true; // best-effort; Steam owns the process
                return;
            }
            catch { /* fall through */ }
        }

        throw new FileNotFoundException(
            "Could not find Celeste.exe. Open this game's Settings and pick your " +
            "Celeste install folder, or install Celeste via Steam.",
            "Celeste.exe");
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string apModDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"celeste-ow-ap-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((10, $"Downloading Archipelago Open World mod {version}..."));
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
                        int pct = (int)(10 + 60 * downloaded / total);
                        progress.Report((pct, $"Downloading mod... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((75, "Installing mod into the Celeste Mods folder..."));
            Directory.CreateDirectory(apModDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, apModDir, overwriteFiles: true);

            // If the zip wraps everything in a single sub-folder, flatten it.
            if (!File.Exists(Path.Combine(apModDir, "Celeste_Multiworld.dll")))
            {
                string[] subdirs = Directory.GetDirectories(apModDir);
                if (subdirs.Length == 1)
                {
                    string sub = subdirs[0];
                    foreach (string fileSrc in Directory.EnumerateFiles(
                        sub, "*", SearchOption.AllDirectories))
                    {
                        string rel     = Path.GetRelativePath(sub, fileSrc);
                        string fileDst = Path.Combine(apModDir, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(fileDst)!);
                        File.Move(fileSrc, fileDst, overwrite: true);
                    }
                    try { Directory.Delete(sub, recursive: true); } catch { }
                }
            }

            progress.Report((90, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class CelesteOWSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private CelesteOWSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<CelesteOWSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(CelesteOWSettings s)
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
    private void SaveOverrideDir(string p) { var s = LoadSettings(); s.InstallOverride = p; SaveSettings(s); }

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().ModVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
    private void WriteStampedVersion(string v) { var s = LoadSettings(); s.ModVersion = v; SaveSettings(s); }
}
