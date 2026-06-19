using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core;

// The launcher project sets BOTH UseWPF and UseWindowsForms=true, so several UI
// type names (Color, Brushes, Button, TextBox, HorizontalAlignment) collide
// between WPF and WinForms. The project's GlobalUsings.cs already aliases each of
// these to its WPF type globally, so this file relies on those — no local aliases
// (a local alias duplicating a global one is CS1537).

namespace LauncherV2.Plugins.UFO50;

// ═══════════════════════════════════════════════════════════════════════════════
// UFO50Plugin — install / launch for "UFO 50" (Mossmouth, 2024) played through
// the UFO-50-Archipelago patch, which modifies the Game Maker bytecode and injects
// the gm-apclientpp native AP client DLL. This is a NATIVE "ConnectsItself"
// integration: the patched game speaks to the AP server itself from its own
// in-game connection screen — no emulator, no Lua bridge, no launcher-held
// ApClient on the slot.
//
// ── HONEST REALITY CHECK (2026-06-15, verified online) ──────────────────────
// UFO 50 uses a BINARY-PATCH + DLL approach rather than a mod loader in the
// traditional sense. The base game is the user's own legally-owned copy (Steam
// appid 2147860). The Archipelago support is delivered by TWO repos that must
// work together:
//
//   * UFO-50-Archipelago/Archipelago — the apworld fork. Releases ship a .apworld
//     file (latest verified tag 0.2.1, "Fixed Night Manor Cherry (again)") that
//     goes into the Archipelago custom_worlds folder so the user can generate
//     multiworld seeds. Includes 4 fully-implemented sub-games (Barbuta, Night
//     Manor, Porgy, Vainger) plus cartridge-unlock logic for all 50 games.
//     AP game string = "UFO 50" (verified from worlds/ufo50/docs/setup_en.md).
//
//   * UFO-50-Archipelago/Patch — the binary patch. Latest release
//     ufo-50-basepatch-alpha-1.4.2 targets UFO 50 v1.7.0.2. The patch modifies
//     data.win (Game Maker bytecode) and supplies gm-apclientpp.dll (the native
//     AP client library). The patched game connects to the AP server at startup
//     via its own in-game screen (no config file the launcher can pre-write;
//     verified from the setup guide). Steam DRM files (steam_api64.dll,
//     Steamworks_x64.dll) must be deleted before patching — the patched build
//     runs without Steam.
//
//   * WHAT THE USER MUST DO (irreducible manual steps — the launcher automates
//     the rest):
//     1. Copy their UFO 50 install to a SEPARATE folder (the patch modifies
//        data.win; the original must be kept safe for future updates).
//     2. Download and extract the patch release zip into that folder.
//     3. Delete steam_api64.dll and Steamworks_x64.dll from the folder.
//     4. Rename data.win to original_data.win (if not already done) and apply
//        ufo_50_basepatch.bsdiff4 using bspatch (or the bundled patcher).
//     5. Drop the .apworld file into Archipelago's custom_worlds folder.
//     6. Launch ufo50.exe — it opens an in-game AP connection screen where the
//        player types the server/slot/password.
//
//   * NO EXTERNAL AP CLIENT. The patched game connects to AP internally via
//     gm-apclientpp.dll — there is no separate "UFO 50 Client" to launch. The
//     earlier setup guide mentioning "UFO 50 Client" referred to a workflow that
//     has been superseded; the current verified flow (setup_en.md) uses the mod
//     loader + in-game connection screen.
//
//   * BSPATCH COMPLEXITY — applying the binary patch requires xdelta3 or bspatch,
//     which this launcher does not bundle. Automating this step would require
//     shipping a third-party binary patcher. Given the manual copy-first
//     requirement and the patch's evolving alpha state, this version of the
//     plugin GUIDES the patching steps rather than automating them. The launcher
//     CAN: detect the patched install (presence of gm-apclientpp.dll and a
//     modified data.win), download the patch zip for the user so they have it
//     ready, and launch the patched ufo50.exe.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT a patched UFO 50 install: looks for a folder containing both
//      ufo50.exe and gm-apclientpp.dll (the DLL is injected by the patch, so
//      its presence is the reliable signal that patching has been done). A
//      manual install-dir override (folder picker) is supported and takes
//      precedence; it is validated and stored in this plugin's own sidecar
//      (Games/ROMs/ufo50/ufo50_launcher.json — Core/SettingsStore is NOT
//      modified).
//   2. INSTALL/UPDATE — Downloads the latest patch release zip from
//      UFO-50-Archipelago/Patch into Games/UFO50/ and the latest apworld from
//      UFO-50-Archipelago/Archipelago so the user has them ready. Does NOT
//      apply the patch automatically (requires bspatch + copy step). Shows
//      clear, numbered guided steps in the settings panel.
//   3. LAUNCH — Runs ufo50.exe from the detected/override install. ConnectsItself
//      = true (the game's in-game client owns the slot — the launcher must NOT
//      hold its own ApClient on the same slot). SupportsStandalone = true (plain
//      UFO 50 runs fine without AP when launched from its own folder).
//      No connection prefill (the in-game screen handles credentials).
//      The settings panel surfaces the session's server/slot for the user to
//      type into that screen.
//
// ── REPO REFERENCES (verified 2026-06-15) ─────────────────────────────────────
//   apworld fork:  https://github.com/UFO-50-Archipelago/Archipelago
//   patch repo:    https://github.com/UFO-50-Archipelago/Patch
//   latest apworld tag: 0.2.1
//   latest patch tag:   ufo-50-basepatch-alpha-1.4.2 (targets UFO 50 v1.7.0.2)
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class UFO50Plugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const string APWORLD_OWNER   = "UFO-50-Archipelago";
    private const string APWORLD_REPO    = "Archipelago";
    private const string PATCH_OWNER     = "UFO-50-Archipelago";
    private const string PATCH_REPO      = "Patch";

    private const string ApWorldRepoUrl  = "https://github.com/UFO-50-Archipelago/Archipelago";
    private const string PatchRepoUrl    = "https://github.com/UFO-50-Archipelago/Patch";
    private const string SetupGuideUrl   = "https://github.com/UFO-50-Archipelago/Archipelago/blob/main/worlds/ufo50/docs/setup_en.md";
    private const string ArchipelagoSite = "https://archipelago.gg";

    private const string GH_APWORLD_RELEASES_URL =
        "https://api.github.com/repos/UFO-50-Archipelago/Archipelago/releases";
    private const string GH_PATCH_RELEASES_URL =
        "https://api.github.com/repos/UFO-50-Archipelago/Patch/releases";

    /// The game executable inside a patched UFO 50 install.
    private const string GameExeName = "ufo50.exe";

    /// DLL injected by the patch — its presence is the reliable indicator that
    /// the binary patch has been successfully applied.
    private const string PatchMarkerDll = "gm-apclientpp.dll";

    /// Steam appid for UFO 50.
    private const string SteamAppId = "2147860";
    private const string SteamRunUrl = "steam://rungameid/2147860";

    /// Folder name under steamapps\common\.
    private const string SteamCommonFolderName = "UFO 50";

    /// Name of the apworld file the releases ship.
    private const string ApWorldFileName = "ufo_50.apworld";

    /// Pinned fallbacks when the GitHub API is unreachable (verified 2026-06-15).
    private const string FallbackApWorldVersion = "0.2.1";
    private const string FallbackPatchVersion   = "ufo-50-basepatch-alpha-1.4.2";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(20),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "ufo50";
    public string DisplayName => "UFO 50";
    public string Subtitle    => "Native PC · binary patch";

    /// EXACT AP game string — verified against worlds/ufo50/docs/setup_en.md and
    /// the UFO-50-Archipelago/Archipelago apworld fork (worlds/ufo50/__init__.py).
    public string ApWorldName => "UFO 50";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "ufo50.png");

    public string ThemeAccentColor => "#1A3A6E";   // retro midnight blue
    public string[] GameBadges     => new[] { "Steam · needs patch" };

    public string Description =>
        "UFO 50 is a collection of 50 retro-styled games developed by the " +
        "Spelunky creators (Mossmouth, 2024), spanning every genre from " +
        "platformers to strategy to RPGs. In the Archipelago integration, each " +
        "of the 50 games is unlocked as a cartridge item — you must receive a " +
        "game's cartridge before you can play it. Four games (Barbuta, Night " +
        "Manor, Porgy, Vainger) add their own checks and items to the multiworld. " +
        "The integration works via a binary patch that adds a native AP client " +
        "directly into the game's bytecode. You bring your own copy of UFO 50 " +
        "(Steam) and apply the patch to a separate copy of your install.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" = a patched UFO 50 install is detected or an override folder
    /// containing both ufo50.exe and gm-apclientpp.dll has been set.
    public bool IsInstalled => FindPatchedInstallDir() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Working folder for downloads (patch zip, apworld). NOT the game dir itself
    /// (that is the user's patched copy).
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "UFO50");

    /// Plugin-own sidecar persisting the override install path.
    private string SidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", "ufo50");
    private string SidecarPath
        => Path.Combine(SidecarDir, "ufo50_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The patched game's built-in AP client reports checks, items, and goal
    // completion to the AP server itself — the launcher relays nothing.
    // ConnectsItself = true.
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
    public event Action<int>?    GameExited;
#pragma warning restore CS0067

    // ── IGamePlugin — ConnectsItself / Standalone ─────────────────────────────

    /// The patched game's native AP client owns the slot. The launcher must NOT
    /// hold its own ApClient on the same slot while the game runs — the server
    /// would kick one connection and auto-reconnect would make it a kick-war.
    public bool ConnectsItself    => true;

    /// Plain (unpatched) UFO 50 runs standalone if launched from Steam; the
    /// patched copy also launches fine without connecting to AP.
    public bool SupportsStandalone => true;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        // Installed state: detect presence of the patched install.
        try
        {
            string? patchedDir = FindPatchedInstallDir();
            InstalledVersion = patchedDir != null
                ? (ReadSidecarVersion() ?? "patched")
                : null;
        }
        catch { InstalledVersion = null; }

        // Available: latest apworld release tag.
        try
        {
            string json = await _http.GetStringAsync(GH_APWORLD_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (el.TryGetProperty("tag_name", out var t))
                    {
                        AvailableVersion = t.GetString();
                        break;
                    }
                }
            }
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(GameDirectory);

        // ── Step 1: Download the latest patch release zip ──────────────────
        progress.Report((5, "Checking latest UFO 50 patch release..."));
        string patchZipUrl = await ResolvePatchZipUrlAsync(ct);

        string patchZipPath = Path.Combine(GameDirectory, "ufo50_patch.zip");
        progress.Report((10, "Downloading patch zip..."));
        await DownloadFileAsync(patchZipUrl, patchZipPath, 10, 45, progress, ct);
        progress.Report((50, "Patch zip saved to: " + patchZipPath));

        // ── Step 2: Download the latest apworld ───────────────────────────
        progress.Report((55, "Checking latest UFO 50 apworld release..."));
        var (apworldVersion, apworldUrl) = await ResolveApWorldAsync(ct);
        AvailableVersion = apworldVersion;

        if (apworldUrl != null)
        {
            string apworldPath = Path.Combine(GameDirectory, ApWorldFileName);
            progress.Report((60, "Downloading apworld..."));
            try
            {
                byte[] apworldBytes = await _http.GetByteArrayAsync(apworldUrl, ct);
                await File.WriteAllBytesAsync(apworldPath, apworldBytes, ct);
                progress.Report((85, "apworld saved — copy it to your Archipelago custom_worlds folder."));
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                progress.Report((85, "Could not download apworld — get it from: " + ApWorldRepoUrl + "/releases"));
            }
        }

        // Stamp version for the tile display.
        WriteSidecarVersion(apworldVersion ?? FallbackApWorldVersion);
        InstalledVersion = apworldVersion ?? FallbackApWorldVersion;

        progress.Report((100,
            "Downloads ready in " + GameDirectory + ". " +
            "IMPORTANT: to finish setup, copy your UFO 50 folder to a new location, " +
            "extract the patch zip into it, delete steam_api64.dll + Steamworks_x64.dll, " +
            "rename data.win to original_data.win, apply ufo_50_basepatch.bsdiff4 using " +
            "bspatch, then copy " + ApWorldFileName + " into your Archipelago custom_worlds folder. " +
            "See Settings for step-by-step instructions and links."));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return FindPatchedInstallDir() != null;
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // The patched game reads credentials from its own in-game connection
        // screen — there is no config file to pre-write. The settings panel
        // surfaces session.ServerUri and session.SlotName for the user to type in.
        return LaunchGameInternal();
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
        => LaunchGameInternal();

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── ValidateExistingInstall ───────────────────────────────────────────────

    public string? ValidateExistingInstall(string folder)
    {
        if (!File.Exists(Path.Combine(folder, GameExeName)))
            return "ufo50.exe not found in that folder.";
        if (!File.Exists(Path.Combine(folder, PatchMarkerDll)))
            return "gm-apclientpp.dll not found — this folder does not appear to be " +
                   "a patched UFO 50 install. Apply the UFO 50 Archipelago patch first " +
                   "(see Settings for instructions), then point the launcher here.";
        return null; // valid
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xE0, 0xAA, 0x30));

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Status ────────────────────────────────────────────────────────
        string? patchedDir = FindPatchedInstallDir();
        bool    ready      = patchedDir != null;

        panel.Children.Add(new TextBlock
        {
            Text       = ready ? "Patched install detected" : "Patched install NOT found",
            FontSize   = 11,
            Foreground = ready ? success : warn,
            Margin     = new Thickness(0, 0, 0, ready ? 4 : 8),
        });
        if (ready)
        {
            panel.Children.Add(new TextBlock
            {
                Text       = patchedDir,
                FontSize   = 10, Foreground = muted, TextWrapping = TextWrapping.Wrap,
                Margin     = new Thickness(0, 0, 0, 12),
            });
        }

        // ── Override install folder ───────────────────────────────────────
        panel.Children.Add(SectionHeader("PATCHED INSTALL FOLDER", muted));

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text        = patchedDir ?? LoadOverridePath() ?? "",
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var browseBtn = new Button
        {
            Content     = "Browse...", Width = 90,
            Padding     = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        browseBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select your patched UFO 50 folder (must contain ufo50.exe + gm-apclientpp.dll)",
                InitialDirectory = patchedDir ?? AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() != true) return;
            string chosen = dlg.FolderName;
            string? err   = ValidateExistingInstall(chosen);
            if (err != null)
            {
                System.Windows.MessageBox.Show(err, "UFO 50 — invalid folder",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            SaveOverridePath(chosen);
            dirBox.Text = chosen;
        };
        DockPanel.SetDock(browseBtn, Dock.Right);
        dirRow.Children.Add(browseBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);
        panel.Children.Add(new TextBlock
        {
            Text = "Point this at the folder containing ufo50.exe AND gm-apclientpp.dll " +
                   "(the DLL confirms the binary patch was applied).",
            FontSize = 10, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin   = new Thickness(0, 4, 0, 16),
        });

        // ── Setup steps ───────────────────────────────────────────────────
        panel.Children.Add(SectionHeader("SETUP STEPS (one-time)", muted));

        var steps = new[]
        {
            "1. Own UFO 50 on Steam (appid 2147860).",
            "2. Copy your UFO 50 Steam folder to a NEW location (the patch modifies " +
                "data.win — keep the original for future Steam updates).",
            "3. Click 'Install / Update' above — the launcher downloads the patch zip " +
                "and the .apworld file into its Games/UFO50/ folder.",
            "4. Extract the patch zip into your COPIED UFO 50 folder (merge, overwrite).",
            "5. Delete steam_api64.dll and Steamworks_x64.dll from the copied folder " +
                "(the patched build runs without Steam).",
            "6. If original_data.win does not exist: rename data.win to original_data.win.",
            "7. Apply ufo_50_basepatch.bsdiff4 using bspatch (or the patcher bundled in " +
                "the zip, if present). This produces the patched data.win.",
            "8. Copy ufo_50.apworld into your Archipelago custom_worlds folder " +
                @"(default: C:\ProgramData\Archipelago\custom_worlds\).",
            "9. Browse to the copied+patched folder above and click Play.",
           "10. UFO 50 opens its own AP connection screen — type your server, slot name, " +
                "and password there. The launcher does NOT pre-fill these fields.",
        };
        foreach (string step in steps)
        {
            panel.Children.Add(new TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 5),
            });
        }
        panel.Children.Add(new TextBlock
        {
            Text = "When the game is updated via Steam, repeat steps 2–7 with the new version.",
            FontSize = 10, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin   = new Thickness(0, 4, 0, 16),
        });

        // ── Downloads folder ──────────────────────────────────────────────
        panel.Children.Add(SectionHeader("DOWNLOADS FOLDER", muted));
        panel.Children.Add(new TextBlock
        {
            Text = GameDirectory, FontSize = 10, Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 16),
        });

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(SectionHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("UFO 50 Archipelago (setup guide) ↗", SetupGuideUrl),
            ("UFO 50 Patch releases ↗",           PatchRepoUrl + "/releases"),
            ("UFO 50 APWorld releases ↗",          ApWorldRepoUrl + "/releases"),
            ("Archipelago Official ↗",             ArchipelagoSite),
        })
        {
            string u = url;
            var link = new Button
            {
                Content         = label,
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding         = new Thickness(0, 2, 0, 2),
                Background      = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize        = 12,
                Margin          = new Thickness(0, 0, 0, 4),
                Foreground      = new SolidColorBrush(Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor          = System.Windows.Input.Cursors.Hand,
            };
            link.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(u) { UseShellExecute = true });
                }
                catch { }
            };
            panel.Children.Add(link);
        }

        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_APWORLD_RELEASES_URL, ct);
            using var doc  = JsonDocument.Parse(json);
            var items = new List<NewsItem>();
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                string tag     = el.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                string title   = el.TryGetProperty("name",     out var n) ? n.GetString() ?? tag : tag;
                string body    = el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "";
                string? url    = el.TryGetProperty("html_url", out var u) ? u.GetString() : null;

                items.Add(new NewsItem(
                    Title:   title,
                    Body:    body,
                    Version: tag,
                    Date:    date,
                    Url:     url
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// Find a patched UFO 50 install: check the override path first, then try
    /// the Steam install location, then Steam default. Returns null if none found
    /// or if the found folder is missing the patch marker DLL.
    private string? FindPatchedInstallDir()
    {
        // 1. Manual override takes precedence.
        string? overridePath = LoadOverridePath();
        if (overridePath != null && IsPatchedDir(overridePath))
            return overridePath;

        // 2. Try to find via Steam registry.
        string? steamDir = FindSteamInstallDir();
        if (steamDir != null && IsPatchedDir(steamDir))
            return steamDir;

        return null;
    }

    private static bool IsPatchedDir(string dir)
        => File.Exists(Path.Combine(dir, GameExeName))
        && File.Exists(Path.Combine(dir, PatchMarkerDll));

    /// Locate the Steam UFO 50 install via the Windows registry and
    /// libraryfolders.vdf. Best-effort — returns null on any failure.
    private static string? FindSteamInstallDir()
    {
        try
        {
            string? steamPath = null;
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Valve\Steam"))
            {
                steamPath = key?.GetValue("SteamPath") as string;
            }
            if (steamPath == null)
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\WOW6432Node\Valve\Steam");
                steamPath = key?.GetValue("InstallPath") as string;
            }
            if (steamPath == null) return null;

            string vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdfPath)) return null;

            var libraryRoots = new List<string> { steamPath };
            foreach (string line in File.ReadAllLines(vdfPath))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase))
                {
                    // Valve key-value: "path"    "/some/path"
                    int first = trimmed.IndexOf('"', 6);
                    if (first < 0) continue;
                    int second = trimmed.IndexOf('"', first + 1);
                    int third  = trimmed.IndexOf('"', second + 1);
                    if (third < 0) continue;
                    int fourth = trimmed.IndexOf('"', third + 1);
                    if (fourth < 0) continue;
                    string path = trimmed.Substring(third + 1, fourth - third - 1)
                                         .Replace(@"\\", @"\");
                    if (!string.IsNullOrWhiteSpace(path))
                        libraryRoots.Add(path);
                }
            }

            foreach (string root in libraryRoots)
            {
                string candidate = Path.Combine(root, "steamapps", "common", SteamCommonFolderName);
                if (File.Exists(Path.Combine(candidate, GameExeName)))
                    return candidate; // may or may not be patched — caller checks
            }
        }
        catch { /* Steam detection is best-effort */ }

        return null;
    }

    private Task LaunchGameInternal()
    {
        string? patchedDir = FindPatchedInstallDir();
        if (patchedDir != null)
        {
            string exePath = Path.Combine(patchedDir, GameExeName);
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exePath,
                WorkingDirectory = patchedDir,
                UseShellExecute  = false,
            }) ?? throw new InvalidOperationException("Failed to start UFO 50.");

            _gameProcess = proc;
            IsRunning    = true;
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) => { IsRunning = false; };
            return Task.CompletedTask;
        }

        // No patched install found — fall back to Steam.
        string? steamDir = FindSteamInstallDir();
        if (steamDir != null)
        {
            // Unpatched Steam install: launch via steam:// URI for the plain game.
            Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
            return Task.CompletedTask;
        }

        throw new InvalidOperationException(
            "UFO 50 patched install not found. " +
            "Apply the Archipelago patch to a copy of your UFO 50 folder, " +
            "then set that folder in this game's Settings tab.");
    }

    /// Resolve the latest patch zip URL from GitHub releases.
    private async Task<string> ResolvePatchZipUrlAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_PATCH_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var release in doc.RootElement.EnumerateArray())
                {
                    if (!release.TryGetProperty("assets", out var assets)) continue;
                    foreach (var asset in assets.EnumerateArray())
                    {
                        string? name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                        string? url  = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                        if (name != null && url != null
                            && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                            && name.Contains("ufo", StringComparison.OrdinalIgnoreCase))
                        {
                            return url;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* fall through to pinned fallback */ }

        // Pinned fallback (verified alive 2026-06-15).
        return PatchRepoUrl + "/releases/download/" + FallbackPatchVersion
               + "/" + FallbackPatchVersion + ".zip";
    }

    /// Resolve the latest apworld URL and version from GitHub releases.
    private async Task<(string Version, string? Url)> ResolveApWorldAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_APWORLD_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var release in doc.RootElement.EnumerateArray())
                {
                    string tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                    if (!release.TryGetProperty("assets", out var assets)) continue;
                    foreach (var asset in assets.EnumerateArray())
                    {
                        string? name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                        string? url  = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                        if (name != null && url != null
                            && name.EndsWith(".apworld", StringComparison.OrdinalIgnoreCase))
                        {
                            return (tag, url);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* fall through */ }

        // Pinned fallback.
        string fallbackUrl = ApWorldRepoUrl + "/releases/download/" + FallbackApWorldVersion
                             + "/" + ApWorldFileName;
        return (FallbackApWorldVersion, fallbackUrl);
    }

    private async Task DownloadFileAsync(
        string url, string destPath,
        int pctStart, int pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        using var response = await _http.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long total      = response.Content.Headers.ContentLength ?? -1;
        long downloaded = 0;
        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destPath);
        var buf = new byte[81920];
        int read;
        while ((read = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, read), ct);
            downloaded += read;
            if (total > 0)
            {
                int pct = pctStart + (int)((pctEnd - pctStart) * downloaded / total);
                progress.Report((pct, $"Downloading... {downloaded / 1_000_000} MB"));
            }
        }
    }

    // ── Sidecar helpers ───────────────────────────────────────────────────────

    private sealed class Sidecar
    {
        public string? OverridePath { get; set; }
        public string? Version      { get; set; }
    }

    private Sidecar LoadSidecar()
    {
        try
        {
            if (!File.Exists(SidecarPath)) return new Sidecar();
            string json = File.ReadAllText(SidecarPath);
            return JsonSerializer.Deserialize<Sidecar>(json) ?? new Sidecar();
        }
        catch { return new Sidecar(); }
    }

    private void SaveSidecar(Sidecar s)
    {
        try
        {
            Directory.CreateDirectory(SidecarDir);
            File.WriteAllText(SidecarPath, JsonSerializer.Serialize(s));
        }
        catch { }
    }

    private string? LoadOverridePath() => LoadSidecar().OverridePath;
    private void    SaveOverridePath(string path)
    {
        var s = LoadSidecar();
        s.OverridePath = path;
        SaveSidecar(s);
    }

    private string? ReadSidecarVersion() => LoadSidecar().Version;
    private void    WriteSidecarVersion(string version)
    {
        var s = LoadSidecar();
        s.Version = version;
        SaveSidecar(s);
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private static TextBlock SectionHeader(string text, SolidColorBrush fg)
        => new()
        {
            Text       = text,
            FontSize   = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = fg,
            Margin     = new Thickness(0, 0, 0, 8),
        };
}
