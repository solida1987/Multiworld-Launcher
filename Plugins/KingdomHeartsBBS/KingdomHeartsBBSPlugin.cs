using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using LauncherV2.Core;

// NOTE on type qualification (BUILD GOTCHA — CS0104 / CS1537):
// The launcher project sets BOTH <UseWPF>true</UseWPF> and
// <UseWindowsForms>true</UseWindowsForms>. That makes a long list of simple type
// names ambiguous between WPF and WinForms. To avoid CS0104 this file does NOT
// do `using System.Windows.Controls;` / `using System.Windows.Media;` — every
// WPF UI type below is written FULLY QUALIFIED. It also does NOT declare any
// file-level `using X = System.Windows...;` alias (CS1537 — GlobalUsings.cs
// already aliases the short names; a local alias would conflict).

namespace LauncherV2.Plugins.KingdomHeartsBBS;

// ═══════════════════════════════════════════════════════════════════════════════
// KingdomHeartsBBSPlugin — detect / guide / launch for
// "Kingdom Hearts: Birth by Sleep" (PC via KINGDOM HEARTS HD 2.8 Final Chapter
// Prologue) through its COMMUNITY Archipelago integration.
//
// ── CONFIRMED AP WORLD ────────────────────────────────────────────────────────
//   Community apworld by gaithernOrg:
//   https://github.com/gaithernOrg/ArchipelagoKHBBS
//   AP game string: "Kingdom Hearts Birth by Sleep"
//   This is a COMMUNITY world (not in AP-main). Players drop the .apworld file
//   into their Archipelago install's "worlds" folder before generating.
//
// ── WHAT KIND OF INTEGRATION IS THIS? ────────────────────────────────────────
//   Native PC — "ConnectsItself" in the OpenKH family (same pattern as KH1 and
//   KH2). The pieces a player needs are:
//
//   * THE BASE GAME — the player's own paid copy of "KINGDOM HEARTS HD 2.8 FINAL
//     CHAPTER PROLOGUE" on PC:
//       · Steam appid 1086940
//       · Epic Games Store: KH2.8 / "Kingdom Hearts HD 2.8 Final Chapter Prologue"
//     The game contains "Kingdom Hearts Birth by Sleep FINAL MIX.exe". This is
//     paid software — the launcher detects it but NEVER ships or downloads it.
//
//   * OpenKH Mod Manager (OpenKH/OpenKh) — the modding toolkit used to patch and
//     manage mods for PC Kingdom Hearts titles. The player must install OpenKH,
//     configure it for KH2.8, and add the BBS AP mod through it. This step is
//     interactive and is NOT something the launcher can drive headlessly — it is
//     GUIDED, not faked.
//
//   * THE ARCHIPELAGO MOD — distributed via the gaithernOrg/ArchipelagoKHBBS
//     GitHub repository. Randomizes chests, stickers, bonus rewards, and story
//     popup rewards. The mod is installed through OpenKH Mod Manager.
//
//   * CONNECTION — the AP slot connection is entered in the game's built-in
//     Archipelago client (BBS received native AP support via the community mod).
//     The launcher does NOT hold an ApClient on the slot. Hence ConnectsItself =
//     true.
//
// ── WHAT THIS PLUGIN HONESTLY DOES ───────────────────────────────────────────
//   1. DETECT the Steam install of KH HD 2.8 (appid 1086940) via the standard
//      registry → libraryfolders.vdf → appmanifest_1086940.acf pipeline, and
//      also probe the Epic Games Store manifests (read-only) as a fallback.
//   2. GUIDE the player through the one-time setup: install OpenKH, add the BBS
//      AP mod, configure the .apworld, and generate through Archipelago.
//   3. LAUNCH the game (via the Steam URI steam://rungameid/1086940 or the
//      detected exe) with one click.
//   4. SURFACE the session host/slot so the player can enter them in-game.
//   5. NEVER modify the Steam or Epic copy; NEVER write to ProgramData.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class KingdomHeartsBBSPlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    /// Steam appid for KINGDOM HEARTS HD 2.8 FINAL CHAPTER PROLOGUE (which
    /// contains Birth by Sleep FINAL MIX). VERIFIED on SteamDB.
    private const string SteamAppId       = "1086940";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    /// The Steam install sub-folder name for KH HD 2.8.
    private const string SteamCommonFolderName = "KINGDOM HEARTS HD 2.8 Final Chapter Prologue";

    /// Candidate exe names inside the KH 2.8 install.
    private static readonly string[] GameExeNames =
    {
        "Kingdom Hearts Birth by Sleep FINAL MIX.exe",
        "KINGDOM HEARTS HD 2.8 Final Chapter Prologue.exe",
        "KHBBS.exe",
    };

    /// Community apworld GitHub repo (for news + links).
    private const string ApWorldRepoUrl  = "https://github.com/gaithernOrg/ArchipelagoKHBBS";
    private const string ApWorldApiUrl   = "https://api.github.com/repos/gaithernOrg/ArchipelagoKHBBS/releases";

    /// Default Archipelago install root (READ-ONLY — never written).
    private const string DefaultArchipelagoRoot = @"C:\ProgramData\Archipelago";

    private const string ArchipelagoSite     = "https://archipelago.gg";
    private const string OpenKhUrl           = "https://github.com/OpenKH/OpenKh/releases";
    private const string ApReleasesUrl       = "https://github.com/ArchipelagoMW/Archipelago/releases";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(2),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "khbbs";
    public string DisplayName => "Kingdom Hearts: Birth by Sleep";
    public string Subtitle    => "Native PC · OpenKH mod";

    /// EXACT AP game string — from gaithernOrg/ArchipelagoKHBBS.
    public string ApWorldName => "Kingdom Hearts Birth by Sleep";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "khbbs.png");

    public string ThemeAccentColor => "#5A3080";   // BBS purple / Terra's armor

    public string[] GameBadges => new[] { "Requires KH HD 2.8" };

    public string Description =>
        "Kingdom Hearts: Birth by Sleep is the 2010 PSP prequel to the Kingdom Hearts " +
        "series, telling the intertwined stories of Terra, Aqua, and Ventus across " +
        "the classic Disney worlds before the events of the original game. The PC " +
        "version ships as part of KINGDOM HEARTS HD 2.8 FINAL CHAPTER PROLOGUE (Steam " +
        "appid 1086940 / Epic). Through the community Archipelago mod (OpenKH-based), " +
        "chests, stickers, bonus rewards, and story popup rewards join the multiworld " +
        "pool across all three protagonists' scenarios. You bring your own copy of KH " +
        "HD 2.8; you install the AP mod via OpenKH Mod Manager; and the in-game AP " +
        "client connects you to the multiworld. The launcher detects your install, " +
        "guides the setup, and launches the game.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion => null;   // no single combined version stamp
    public string? AvailableVersion => null;

    public bool IsInstalled => ResolveGameDir() != null;
    public bool IsRunning   { get; private set; }

    /// Returns the detected/override KH 2.8 root directory, or "" if unknown.
    public string GameDirectory => ResolveGameDir() ?? "";

    // ── Paths — self-contained sidecar ────────────────────────────────────────

    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "khbbs_launcher.json");

    // ── Internal override state ────────────────────────────────────────────────

    private string?  _overrideRootDir;
    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The AP slot connection is entered in-game (via the community mod client).
    // The launcher holds no ApClient on the slot. These exist for interface
    // compatibility only (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── ConnectsItself ────────────────────────────────────────────────────────

    /// The in-game AP client (delivered via the community mod) holds the slot
    /// connection. The launcher must not also sit an ApClient on the same slot.
    public bool ConnectsItself     => true;

    /// KH BBS is a full standalone game; plain (non-AP) play is fully supported.
    public bool SupportsStandalone => true;

    // ── Constructor ───────────────────────────────────────────────────────────

    public KingdomHeartsBBSPlugin()
    {
        try
        {
            var s = LoadSettings();
            if (!string.IsNullOrWhiteSpace(s.RootDirOverride) &&
                Directory.Exists(s.RootDirOverride))
                _overrideRootDir = s.RootDirOverride;
        }
        catch { /* fall back to detection only */ }
    }

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public Task CheckForUpdateAsync(CancellationToken ct = default)
        => Task.CompletedTask;

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((10, "Checking for your Kingdom Hearts HD 2.8 install..."));

        string? gameDir = ResolveGameDir();
        var sb = new StringBuilder();

        if (gameDir != null)
            sb.Append("Kingdom Hearts HD 2.8 detected at \"").Append(gameDir).Append("\".\n\n");
        else
            sb.Append("Kingdom Hearts HD 2.8 was not detected. Install it via Steam " +
                      "(appid 1086940) or Epic Games Store, or set the folder in Settings.\n\n");

        sb.Append("SETUP STEPS:\n")
          .Append("1. Install Kingdom Hearts HD 2.8 Final Chapter Prologue (Steam appid ")
          .Append(SteamAppId).Append(" or Epic).\n")
          .Append("2. Download the community Archipelago .apworld from the GitHub releases ")
          .Append("page and drop it into your Archipelago install's \"worlds\" folder.\n")
          .Append("3. Install OpenKH Mod Manager (openkhproject.github.io / GitHub releases) ")
          .Append("and configure it for your KH 2.8 install (it patches the game's executables).\n")
          .Append("4. In OpenKH Mod Manager, add the BBS Archipelago mod from ")
          .Append(ApWorldRepoUrl).Append(" and apply it.\n")
          .Append("5. Generate your seed through Archipelago (archipelago.gg / local generate), ")
          .Append("select \"Kingdom Hearts Birth by Sleep\" as your game, upload the .yaml.\n")
          .Append("6. Press Play here to launch the game, then enter your AP server address, ")
          .Append("slot name, and password in-game when prompted by the AP client.\n\n")
          .Append("The connection is entered inside the game, not in the launcher. The ")
          .Append("session host and slot name are shown on the Play tab for easy copying.");

        progress.Report((100, sb.ToString()));
        return Task.CompletedTask;
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public Task<bool> VerifyInstallAsync(CancellationToken ct = default)
        => Task.FromResult(IsInstalled);

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        _ = session; // connection is entered in-game via the AP mod client
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
        _gameProcess = null;
        IsRunning    = false;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (game holds its own slot connection) ───────────────

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
        var success = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0xD0, 0xA0, 0x40));
        var linkClr = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF));

        var panel = new System.Windows.Controls.StackPanel
                    { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "Kingdom Hearts: Birth by Sleep requires your own copy of KINGDOM HEARTS " +
                "HD 2.8 FINAL CHAPTER PROLOGUE (Steam appid " + SteamAppId + " or Epic), " +
                "the OpenKH Mod Manager to apply the AP mod, and the community .apworld " +
                "file dropped into your Archipelago install's worlds folder. The AP " +
                "connection is entered inside the game — the launcher does not connect to " +
                "your slot. It detects your install, guides setup, and launches the game.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: KH 2.8 install ───────────────────────────────────────
        panel.Children.Add(SectionHeader("KINGDOM HEARTS HD 2.8 INSTALL", muted));

        string? gameDir = ResolveGameDir();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = gameDir != null
                ? "Game detected (appid " + SteamAppId + "):\n" + gameDir
                : "Not detected via Steam or Epic. Install Kingdom Hearts HD 2.8 " +
                  "(appid " + SteamAppId + "), or set its folder below.",
            FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel
                     { Margin = new System.Windows.Thickness(0, 0, 0, 14) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = _overrideRootDir ?? gameDir ?? "",
            IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var browseBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        browseBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select your Kingdom Hearts HD 2.8 install folder",
                InitialDirectory = Directory.Exists(_overrideRootDir ?? gameDir ?? "")
                    ? (_overrideRootDir ?? gameDir!)
                    : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                _overrideRootDir = dlg.FolderName;
                dirBox.Text = dlg.FolderName;
                SaveRootDirOverride(dlg.FolderName);
            }
        };
        System.Windows.Controls.DockPanel.SetDock(browseBtn,
            System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(browseBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        // ── Section: Setup steps ──────────────────────────────────────────
        panel.Children.Add(SectionHeader("SETUP GUIDE", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "1) Own and install Kingdom Hearts HD 2.8 Final Chapter Prologue " +
                "(Steam appid " + SteamAppId + " or Epic Games Store).\n" +
                "2) Download the community .apworld from the link below and drop it " +
                "into your Archipelago install's \"worlds\" folder " +
                "(default: C:\\ProgramData\\Archipelago\\worlds).\n" +
                "3) Install OpenKH Mod Manager, configure it for your KH 2.8 install, " +
                "add the BBS Archipelago mod from GitHub, and apply it.\n" +
                "4) Generate your seed through Archipelago (website or locally), " +
                "selecting \"Kingdom Hearts Birth by Sleep\" as your game.\n" +
                "5) Press Play here to launch the game, then enter your server, slot " +
                "name, and password when prompted by the in-game AP client.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Links ────────────────────────────────────────────────
        panel.Children.Add(SectionHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("KH BBS Archipelago mod (GitHub) ↗", ApWorldRepoUrl),
            ("OpenKH Mod Manager (GitHub) ↗",     OpenKhUrl),
            ("Archipelago Releases ↗",             ApReleasesUrl),
            ("Archipelago Official ↗",             ArchipelagoSite),
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
                Foreground = linkClr,
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
            string json = await _http.GetStringAsync(ApWorldApiUrl, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) &&
                    d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : "",
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: el.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "",
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var u) ? u.GetString() : null
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartGame()
    {
        string? root = ResolveGameDir();
        string? exe  = null;

        if (root != null)
        {
            foreach (string name in GameExeNames)
            {
                string cand = Path.Combine(root, name);
                if (File.Exists(cand)) { exe = cand; break; }
            }
        }

        if (exe != null)
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = root!,
                UseShellExecute  = true,
            });
            if (proc != null)
            {
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
                catch { /* non-fatal */ }
            }
            return;
        }

        // Fall back to Steam URI when direct exe is not found.
        try
        {
            Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
            IsRunning = true;
        }
        catch
        {
            throw new FileNotFoundException(
                "Could not find the Kingdom Hearts HD 2.8 executable. Set the install " +
                "folder in Settings or install the game via Steam (appid " + SteamAppId + ").",
                GameExeNames[0]);
        }
    }

    // ── Private helpers — install detection ───────────────────────────────────

    private string? ResolveGameDir()
    {
        if (!string.IsNullOrWhiteSpace(_overrideRootDir) &&
            LooksLikeKHBBSRoot(_overrideRootDir))
            return _overrideRootDir;

        try { return DetectSteamKH28Dir() ?? DetectEpicKH28Dir(); }
        catch { return null; }
    }

    private static bool LooksLikeKHBBSRoot(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            foreach (string name in GameExeNames)
                if (File.Exists(Path.Combine(dir, name))) return true;
            return false;
        }
        catch { return false; }
    }

    private static string? DetectSteamKH28Dir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps,
                                           $"appmanifest_{SteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string cand = Path.Combine(common, installDir);
                        if (LooksLikeKHBBSRoot(cand)) return cand;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeKHBBSRoot(conventional)) return conventional;
                }
                catch { /* try next */ }
            }
        }
        return null;
    }

    /// Scan the read-only Epic Games manifest folder for a KH 2.8 entry.
    private static string? DetectEpicKH28Dir()
    {
        try
        {
            string manifestDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "EpicGamesLauncher", "Data", "Manifests");
            if (!Directory.Exists(manifestDir)) return null;

            foreach (string item in Directory.EnumerateFiles(manifestDir, "*.item"))
            {
                try
                {
                    string text = File.ReadAllText(item);
                    if (!text.Contains("2.8", StringComparison.OrdinalIgnoreCase) &&
                        !text.Contains("KH28", StringComparison.OrdinalIgnoreCase) &&
                        !text.Contains("BirthBySleep", StringComparison.OrdinalIgnoreCase))
                        continue;

                    using var doc = JsonDocument.Parse(text);
                    if (!doc.RootElement.TryGetProperty("InstallLocation", out var loc))
                        continue;
                    string? dir = loc.GetString();
                    if (!string.IsNullOrWhiteSpace(dir) && LooksLikeKHBBSRoot(dir))
                        return dir;
                }
                catch { /* corrupt manifest — skip */ }
            }
        }
        catch { /* unreadable — skip */ }
        return null;
    }

    // ── Private helpers — Steam detection (same pattern as KH1/KH2/Civ6) ─────

    private static IEnumerable<string> SteamRoots()
    {
        string? hkcu = ReadRegistryString(Registry.CurrentUser,
            @"Software\Valve\Steam", "SteamPath");
        if (!string.IsNullOrWhiteSpace(hkcu)) yield return hkcu.Replace('/', '\\');

        string? hklm = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm)) yield return hklm;

        string? hklm64 = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm64)) yield return hklm64;

        string? progX86 = Environment.GetFolderPath(
                              Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

    private static IEnumerable<string> SteamLibraryRoots(string steamRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (seen.Add(steamRoot)) yield return steamRoot;

        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        string text;
        try { text = File.ReadAllText(vdf); }
        catch { yield break; }

        const string key = "\"path\"";
        int i = 0;
        while ((i = text.IndexOf(key, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            i += key.Length;
            int open = text.IndexOf('"', i);
            if (open < 0) break;
            int close = text.IndexOf('"', open + 1);
            if (close < 0) break;
            string raw = text.Substring(open + 1, close - open - 1)
                             .Replace("\\\\", "\\").Replace('/', '\\').TrimEnd('\\');
            if (raw.Length > 0 && seen.Add(raw)) yield return raw;
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

    private static string? ReadRegistryString(RegistryKey hive, string subKey,
        string valueName)
    {
        try
        {
            using RegistryKey? k = hive.OpenSubKey(subKey);
            return k?.GetValue(valueName) as string;
        }
        catch { return null; }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class KHBBSSettings
    {
        public string? RootDirOverride { get; set; }
    }

    private KHBBSSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<KHBBSSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveRootDirOverride(string dir)
    {
        try
        {
            var s = LoadSettings();
            s.RootDirOverride = dir;
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal */ }
    }

    // ── UI helper ─────────────────────────────────────────────────────────────

    private static System.Windows.Controls.TextBlock SectionHeader(
        string text, System.Windows.Media.Brush muted)
        => new System.Windows.Controls.TextBlock
        {
            Text = text, FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin = new System.Windows.Thickness(0, 8, 0, 8),
        };
}
