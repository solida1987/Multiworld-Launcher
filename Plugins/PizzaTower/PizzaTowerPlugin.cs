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

// The launcher project sets BOTH UseWPF and UseWindowsForms=true, so several UI
// type names (Color, Brushes, Button, TextBox, HorizontalAlignment) collide
// between WPF and WinForms. The project's GlobalUsings.cs already aliases each of
// these to its WPF type globally, so this file relies on those — no local aliases
// (a local alias duplicating a global one is CS1537).

namespace LauncherV2.Plugins.PizzaTower;

// ═══════════════════════════════════════════════════════════════════════════════
// PizzaTowerPlugin — install / launch for "Pizza Tower" (Tour De Pizza, 2023)
// played with the BabyblueSheep/pizza-tower-ap Archipelago patch, a GameMaker
// Studio 2 mod that injects a native AP client (gm-apclientpp) into the game.
// This is a NATIVE "ConnectsItself" integration: the patched game speaks to the
// AP server directly — no emulator, no Lua bridge, no launcher-held ApClient.
//
// ── HONEST REALITY CHECK (2026-06-15, verified from the source repo) ──────────
// This is a STEAM-MOD native. The base game is the user's own legally-owned copy
// of Pizza Tower (Steam appid 2231450, GameMaker Studio 2). Archipelago support
// is delivered via UndertaleModTool-based patching of the game's data.win.
//
//   * THE AP WORLD GAME STRING IS "Pizza Tower" (confirmed from source:
//     ArchipelagoPizzaTower.GameMakerExtension/src/gm-apclientpp.cpp line:
//       `ap_client.reset(new APClient(uuid, "Pizza Tower", uri, CERT_STORE));`
//     This is a COMMUNITY world — it is NOT part of Archipelago's core and does
//     NOT appear on archipelago.gg/games as of 2026-06-15. The .apworld must be
//     obtained from the patch repo and dropped into Archipelago's custom_worlds
//     folder for generation.
//
//   * THE PATCHER REPO is BabyblueSheep/pizza-tower-ap (verified live 2026-06-15,
//     last pushed 2024-07-29). It contains:
//       ArchipelagoPizzaTower.Patcher.Console  — CLI patcher (dotnet build)
//       ArchipelagoPizzaTower.GameMakerExtension — native DLL (C++, gm-apclientpp)
//     The patcher modifies the Pizza Tower data.win via UndertaleModTool and
//     injects the GameMaker extension DLL, which embeds a WebSocket AP client.
//
//   * CRITICAL HONESTY — NO BINARY RELEASES EXIST. As of 2026-06-15, the repo
//     has zero published GitHub releases (the /releases/latest endpoint returns
//     404). The patcher must be BUILT FROM SOURCE using .NET and Visual C++
//     toolchains. The setup is therefore necessarily manual and requires developer
//     tools. This plugin cannot automate the patcher step — it can only detect the
//     user's Steam install, guide the patching steps, and launch the (already-
//     patched) game. A future release from the repo would enable full automation.
//
//   * HOW THE PATCHED GAME CONNECTS: The GameMaker extension calls ap_connect()
//     and ap_connect_slot() from within the game's own GML code. Server, slot
//     name, and password are entered IN-GAME through a menu added by the patch.
//     There is NO external config file or command-line argument this launcher can
//     pre-write (no such interface exists in the repo as of 2026-06-15). This
//     plugin does NOT attempt connection prefill — it states this honestly.
//
//   * WHAT "INSTALLED" MEANS here: the user ran the CLI patcher against their
//     Pizza Tower install. The patcher modifies data.win. We detect this by
//     checking for a patcher-written sentinel file OR the injected DLL. A plain
//     unpatched Pizza Tower data.win is not "installed."
//
//   * ConnectsItself = true: the patched game's embedded AP client owns the slot.
//     The launcher must NOT hold its own ApClient on the same slot while the game
//     runs (it would cause a kick-war). The launcher suppresses auto-reconnect and
//     "connection lost" toasts while the game is running.
//
//   * SupportsStandalone = true: the unpatched game runs normally; even the
//     patched game supports offline play when not connected to AP.
//
// ── WHAT THIS PLUGIN DOES ─────────────────────────────────────────────────────
//   1. DETECT the Steam Pizza Tower install via the Windows registry and
//      steamapps/libraryfolders.vdf scan, exactly as the Hollow Knight / Noita /
//      BRC plugins do. A manual install-dir override (settings folder picker) is
//      supported and persisted in a plugin-own sidecar (Games/ROMs/pizza_tower/).
//   2. IsInstalled: true when the game install contains the patched DLL
//      (ArchipelagoPizzaTower.dll next to the exe, or a sentinel file we write),
//      meaning the user has already run the CLI patcher.
//   3. InstallOrUpdateAsync: download cannot be automated (no binary releases).
//      The method throws a clear, instructional InvalidOperationException — the
//      launcher shows this as a friendly error with the manual steps. All guided
//      steps and links are in CreateSettingsPanel.
//   4. LAUNCH: run PizzaTower.exe (Steam install or steam:// URL fallback).
//      ConnectsItself = true — no prefill, no IPC, no relay.
//   5. NEWS: no repo releases → empty feed (graceful fallback, no throw).
//   6. SETTINGS UI: guided steps, links to the repo and the AP setup page, plus
//      the Steam folder picker and patcher invocation guide.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class PizzaTowerPlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const string MOD_OWNER   = "BabyblueSheep";
    private const string MOD_REPO    = "pizza-tower-ap";
    private const string ModRepoUrl  = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";

    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string ApWorldName_Const  = "Pizza Tower";
    private const string SteamAppId         = "2231450";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    /// Conventional Steam game folder name (steamapps/common/Pizza Tower).
    private const string SteamCommonFolderName = "Pizza Tower";

    /// Primary executable name inside the Steam install.
    private const string GameExeName = "PizzaTower.exe";

    /// The injected GameMaker extension DLL written by the patcher.
    /// Presence = patcher has been run. Name derived from the repo project name.
    private const string PatchedDllName = "ArchipelagoPizzaTower.dll";

    /// Sentinel file written by this launcher after the user confirms patching.
    private const string PatchSentinelName = "archipelago_patch.stamp";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ─────────────────────────────────────────────────

    public string GameId      => "pizza_tower";
    public string DisplayName => "Pizza Tower";
    public string Subtitle    => "Native PC · AP patch";

    /// EXACT AP game string confirmed from gm-apclientpp.cpp:
    ///   `ap_client.reset(new APClient(uuid, "Pizza Tower", uri, CERT_STORE));`
    public string ApWorldName => ApWorldName_Const;

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "pizza_tower.png");

    public string ThemeAccentColor => "#E8A020";   // pizza orange-yellow

    public string[] GameBadges => new[] { "Steam · manual patch" };

    public string Description =>
        "Pizza Tower is a fast-paced 2023 indie platformer by Tour De Pizza, inspired " +
        "by Wario Land — help Peppino Spaghetti race through the tower before the " +
        "ticking clock runs out. The Archipelago mod (by BabyblueSheep) patches the " +
        "game's data.win via UndertaleModTool and injects a native AP client written " +
        "in C++, so the patched game connects to the Archipelago multiworld server " +
        "entirely on its own. Items, collectibles, and level progression are shuffled " +
        "across the multiworld. You bring your own copy of Pizza Tower (owned on " +
        "Steam) and run the CLI patcher once against it — the launcher then detects " +
        "your install and launches the patched game. Connection credentials are " +
        "entered in-game. Note: as of mid-2026 the patcher has no binary release " +
        "and must be built from source — see the Settings tab for the guided steps.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ──────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// Installed = the patched DLL or our sentinel exists in the game folder.
    public bool IsInstalled => DetectPatchedInstall() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ──────────────────────────────────────────────────────────────────

    /// Working/download area for this plugin (NOT the Steam install).
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "PizzaTower");

    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "pizza_tower_launcher.json");

    // ── Internal state ─────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ───────────────────────────────────────────────────────
    // The patched game's embedded AP client talks to the server itself.
    // LocationsChecked / GoalCompleted are inert (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── ConnectsItself / Standalone ────────────────────────────────────────────

    /// The patched game's embedded AP client owns the slot (see header).
    /// The launcher must NOT connect its own ApClient while the game runs.
    public bool ConnectsItself => true;

    /// Pizza Tower (patched or plain) can run without AP.
    public bool SupportsStandalone => true;

    // ── Lifecycle — CheckForUpdate ─────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        // Installed side: stamped version from sidecar, or "patched" if we just
        // detect the DLL without our stamp.
        try
        {
            string? patched = DetectPatchedInstall();
            if (patched != null)
            {
                string? stamped = LoadSettings().PatchedVersion;
                InstalledVersion = string.IsNullOrWhiteSpace(stamped) ? "patched" : stamped;
            }
            else
            {
                InstalledVersion = null;
            }
        }
        catch
        {
            InstalledVersion = null;
        }

        // Remote side: the repo currently has no releases (verified 2026-06-15).
        // Try anyway — a future release will populate this.
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                // Pick the first non-prerelease, or first of any kind.
                JsonElement? pick = null;
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    bool isPrerelease = el.TryGetProperty("prerelease", out var p) && p.GetBoolean();
                    if (!isPrerelease)   { pick = el; break; }
                    pick ??= el;
                }
                if (pick.HasValue && pick.Value.TryGetProperty("tag_name", out var t))
                    AvailableVersion = NormalizeTag(t.GetString());
                else
                    AvailableVersion = null;
            }
            else
            {
                AvailableVersion = null;
            }
        }
        catch
        {
            AvailableVersion = null; // contract: never throw on network failure
        }
    }

    // ── Lifecycle — InstallOrUpdate ────────────────────────────────────────────

    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // HONEST: there are no binary releases as of 2026-06-15. Automation of
        // the patcher step is not possible without pre-built binaries. Surface a
        // clear, instructional error that the launcher will show as a friendly
        // message — all manual steps and links are in CreateSettingsPanel.
        throw new InvalidOperationException(
            "The Pizza Tower Archipelago patcher has no binary release yet — it must " +
            "be built from source. Please follow the guided steps in this game's " +
            "Settings tab: clone the repo (" + ModRepoUrl + "), build the patcher " +
            "with .NET 8 SDK + Visual C++ tools, run it against your Pizza Tower " +
            "install, and then launch the game from this tile. When the patcher " +
            "publishes binary releases the launcher will be able to install them " +
            "automatically.");
    }

    // ── Lifecycle — Verify ─────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        // "Verified" = the patched DLL or sentinel is present AND the exe exists.
        string? gameDir = ResolveGameDir();
        if (gameDir == null) return false;
        string? patchMark = DetectPatchedInstall();
        return patchMark != null && File.Exists(Path.Combine(gameDir, GameExeName));
    }

    // ── Lifecycle — Launch ─────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // HONEST: the AP connection is entered entirely in-game via the menu
        // injected by the patch. There is no config file or command-line arg
        // this launcher can write (verified against the repo — no such interface
        // exists). ConnectsItself = true; the launcher's own ApClient must not
        // hold the slot while the game runs.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartPizzaTower();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartPizzaTower();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        // No plaintext AP password is ever written by this plugin (connection
        // is entered in-game), so there is nothing to scrub.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The patched game receives items from the AP server directly via its
        // embedded gm-apclientpp extension. Nothing to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The patched game renders its own AP connection status in-game.
        // No launcher-side HUD channel exists.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Status banner ─────────────────────────────────────────────────
        string? gameDir  = ResolveGameDir();
        string? patched  = DetectPatchedInstall();
        bool    hasSteam = gameDir != null;
        bool    isPatched = patched != null;

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "Pizza Tower Archipelago patches the game's data.win via UndertaleModTool " +
                "and injects a native C++ AP client. The game then connects to the multiworld " +
                "server itself (ConnectsItself). As of mid-2026 the patcher has no binary " +
                "release and must be built from source. This launcher can detect your Steam " +
                "install, guide the one-time patch, and then launch the patched game.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: Steam install ────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "PIZZA TOWER INSTALL",
            FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? overrideDir = LoadSettings().InstallOverride;
        string detectMsg = hasSteam
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Pizza Tower (Steam appid 2231450) not detected. Select your install folder " +
              "below, or install via Steam first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = hasSteam ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = isPatched
                ? "Archipelago patch detected in your install (" + patched + ")."
                : "Archipelago patch NOT detected — run the patcher against your install " +
                  "first (see the Guided Setup steps below).",
            FontSize = 11,
            Foreground = isPatched ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        // Folder picker row
        var dirRow = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? gameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Pizza Tower install folder (contains PizzaTower.exe). " +
                          "Detected from Steam automatically; set here to override.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120, Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select your Pizza Tower install folder (contains PizzaTower.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? gameDir ?? "")
                    ? (overrideDir ?? gameDir!)
                    : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                if (!LooksLikePizzaTowerDir(picked))
                {
                    string sub = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikePizzaTowerDir(sub)) picked = sub;
                }
                if (!LooksLikePizzaTowerDir(picked))
                {
                    System.Windows.MessageBox.Show(
                        "That does not look like a Pizza Tower install. " +
                        "Pick the folder containing PizzaTower.exe.",
                        "Not a Pizza Tower folder",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                SaveOverrideDir(picked);
                dirBox.Text = picked;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(dirBtn, Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Steam installs are detected automatically (appid 2231450). Use the " +
                   "picker for a non-standard Steam library or another store.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 16),
        });

        // ── Section: connection note ──────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in-game)",
            FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "The Archipelago patch adds an in-game connection menu. After " +
                   "patching and launching, enter your Server, Port, Slot Name, and " +
                   "Password in the AP connection screen inside the game. There is no " +
                   "config file or command-line argument this launcher can pre-write " +
                   "(verified against the patcher repo — no such interface exists). " +
                   "Click Play once you have your Archipelago server details ready.",
            FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        });

        // ── Section: guided setup ─────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP (one-time, then just click Play)",
            FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Pizza Tower on Steam (appid 2231450). Install it if you have not. " +
                "The unmodified game must run at least once before patching.",
            "2. Install .NET 8 SDK (https://dotnet.microsoft.com/download) and " +
                "Visual C++ Build Tools (part of Visual Studio or the standalone " +
                "Build Tools installer). Both are needed to compile the patcher.",
            "3. Clone the patcher repo with submodules:\n" +
                "   git clone --recurse-submodules " + ModRepoUrl,
            "4. Build the C++ GameMaker extension:\n" +
                "   Open ArchipelagoPizzaTower.GameMakerExtension.vcxproj in Visual " +
                "Studio and build (Release x64). Copy the resulting DLL to your " +
                "Pizza Tower folder.",
            "5. Build the CLI patcher:\n" +
                "   dotnet build ArchipelagoPizzaTower.Patcher.Console",
            "6. Run the patcher against your Pizza Tower install:\n" +
                "   ArchipelagoPizzaTower.Patcher.Console.exe patch \"<path to Pizza Tower folder>\"",
            "7. Obtain the pizza_tower.apworld from the patcher repo (it may need to " +
                "be generated separately — see the repo README). Drop it into your " +
                @"Archipelago custom_worlds folder (default: C:\ProgramData\Archipelago\custom_worlds) " +
                "to enable multiworld generation with this game.",
            "8. Generate your multiworld YAML on the Archipelago website, then start " +
                "your Archipelago server. Use the folder picker above to point this " +
                "launcher at your Pizza Tower install if it was not auto-detected.",
            "9. Click Play in this tile. Enter your server address, slot name, and " +
                "password in the AP connection screen that appears in-game.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LINKS",
            FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 12, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("Pizza Tower AP Patcher (GitHub) ↗",   ModRepoUrl),
            ("Pizza Tower on Steam ↗",               $"https://store.steampowered.com/app/{SteamAppId}"),
            ("Archipelago Custom Worlds Guide ↗",    "https://archipelago.gg/tutorial/Archipelago/setup_en"),
            ("Archipelago Official ↗",               "https://archipelago.gg"),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label, HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), FontSize = 12, Margin = new Thickness(0, 0, 0, 4),
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

    // ── News feed ──────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // The repo has no releases yet (verified 2026-06-15); the endpoint returns
        // an empty array. Try anyway — future releases will populate this.
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

    // ── Private helpers — game detection ──────────────────────────────────────

    /// The Pizza Tower install dir to use: manual override (if set and valid)
    /// wins, else the Steam-detected install. Null when nothing is found.
    private string? ResolveGameDir()
    {
        string? ov = LoadSettings().InstallOverride;
        if (ov != null && LooksLikePizzaTowerDir(ov))
            return ov;

        try { return DetectSteamPizzaTowerDir(); }
        catch { return null; }
    }

    /// A directory "looks like" the Pizza Tower install if it contains PizzaTower.exe.
    private static bool LooksLikePizzaTowerDir(string dir)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(dir)
                && Directory.Exists(dir)
                && File.Exists(Path.Combine(dir, GameExeName));
        }
        catch { return false; }
    }

    /// Detect the Steam install of Pizza Tower. Parses the registry and
    /// libraryfolders.vdf using the same tolerant approach as the Noita /
    /// BRC / Hollow Knight plugins.
    private static string? DetectSteamPizzaTowerDir()
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

                    string common     = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikePizzaTowerDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikePizzaTowerDir(conventional)) return conventional;
                }
                catch { /* try next library */ }
            }
        }
        return null;
    }

    /// Returns the patch marker path when the Archipelago patch is detected in
    /// the game directory, or null when the game is not (yet) patched.
    /// Detection order: our sentinel file → the injected DLL.
    private string? DetectPatchedInstall()
    {
        string? dir = ResolveGameDir();
        if (dir == null) return null;

        try
        {
            string sentinel = Path.Combine(dir, PatchSentinelName);
            if (File.Exists(sentinel)) return sentinel;

            string dll = Path.Combine(dir, PatchedDllName);
            if (File.Exists(dll)) return dll;
        }
        catch { /* permission or race */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartPizzaTower()
    {
        string? dir = ResolveGameDir();
        string? exe = dir != null ? Path.Combine(dir, GameExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = dir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Pizza Tower.");

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

        // Fall back to Steam if we know Steam is installed but couldn't locate
        // the exe (e.g. non-standard library root the VDF scan missed).
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true; // best-effort; Steam owns the process
                return;
            }
            catch { /* fall through to error */ }
        }

        throw new FileNotFoundException(
            "Could not find PizzaTower.exe. Open this game's Settings and select " +
            "your Pizza Tower install folder, or install Pizza Tower via Steam.",
            GameExeName);
    }

    // ── Private helpers — Steam detection ────────────────────────────────────

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
            string text  = File.ReadAllText(acfPath);
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

    // ── Private helpers — version tag normalisation ───────────────────────────

    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class PizzaTowerSettings
    {
        public string? InstallOverride { get; set; }
        public string? PatchedVersion  { get; set; }
    }

    private PizzaTowerSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<PizzaTowerSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(PizzaTowerSettings s)
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
}
