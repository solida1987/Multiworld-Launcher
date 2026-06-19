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

// NOTE on type qualification (BUILD GOTCHA — CS0104):
// This project sets BOTH <UseWPF>true</UseWPF> and <UseWindowsForms>true</UseWindowsForms>.
// That makes a long list of simple type names ambiguous between WPF and WinForms
// (Clipboard, MessageBox, Application, Color, Brush(es), Button, TextBox, CheckBox,
// Orientation, FontWeights, HorizontalAlignment, Cursors, Thickness, …). To avoid
// CS0104 this file deliberately does NOT do `using System.Windows.Controls;` /
// `using System.Windows.Media;` — every WPF UI type below is written FULLY QUALIFIED
// (System.Windows.Controls.*, System.Windows.Media.*, System.Windows.MessageBox, …).
// It also does NOT declare any file-level `using X = System.Windows...;` alias
// (CS1537 — GlobalUsings.cs already aliases the short names; a local alias would
// conflict). Bare names from GlobalUsings, or full qualification, only.

namespace LauncherV2.Plugins.Wargroove2;

// ═══════════════════════════════════════════════════════════════════════════════
// Wargroove2Plugin — detect / guide / launch for "Wargroove 2" (Chucklefish, 2023)
// played through its Archipelago integration.
//
// ── HONEST REALITY CHECK (2026-06-16) ────────────────────────────────────────
//   * AP WORLD — community apworld from FlySniper/Archipelago fork.
//     AP game string: "Wargroove 2" (inferred from fork; verify against
//     worlds/__init__.py when integrating).
//     This is a COMMUNITY apworld — NOT in AP main. The .apworld must be
//     installed by the user into their AP worlds/ folder.
//
//   * THE BASE GAME is the user's own legally-owned Wargroove 2 (Steam appid
//     1664400, Chucklefish). Paid software — the launcher does not ship or
//     recreate it.
//
//   * CONNECTION IS CLIENT-RELAY. Like the original Wargroove, an external
//     Archipelago client bridges the game to the AP server. The player connects
//     by entering server + username IN THE CLIENT — not in the game or via the
//     launcher. Hence ConnectsItself = true.
//
//   * INSTALL strategy: Steam detection (appid 1664400) via registry →
//     libraryfolders.vdf → appmanifest_1664400.acf → steamapps/common.
//     The Archipelago install is READ-ONLY (locate client only).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class Wargroove2Plugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    /// Wargroove 2 Steam appid (verified: store.steampowered.com/app/1664400).
    private const string SteamAppId = "1664400";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    private const string SteamCommonFolderName = "Wargroove 2";
    private const string PreferredGameExeName   = "Wargroove2.exe";
    private const string DefaultArchipelagoRoot = @"C:\ProgramData\Archipelago";
    private const string ArchipelagoLauncherExe = "ArchipelagoLauncher.exe";
    private const string Wargroove2ClientName   = "Wargroove 2 Client";

    private const string SteamStoreUrl   = "https://store.steampowered.com/app/1664400/Wargroove_2/";
    private const string ForkReleasesUrl = "https://github.com/FlySniper/Archipelago/releases";
    private const string ArchipelagoSite = "https://archipelago.gg";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(2),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ─────────────────────────────────────────────────

    public string GameId      => "wargroove_2";
    public string DisplayName => "Wargroove 2";
    public string Subtitle    => "Native PC · Archipelago";
    public string ApWorldName => "Wargroove 2";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "wargroove2.png");

    public string ThemeAccentColor => "#9E2A2B";   // Wargroove 2 crimson banner
    public string[] GameBadges     => new[] { "Requires Wargroove 2 on Steam" };

    public string Description =>
        "Wargroove 2 is Chucklefish's sequel to the award-winning turn-based strategy " +
        "game, introducing new commanders, units, and a branching campaign across three " +
        "warring factions. In the community Archipelago integration (FlySniper/Archipelago), " +
        "campaign progression items and checks join the multiworld pool. You bring your own " +
        "copy of Wargroove 2 (owned on Steam); the Archipelago client manages the connection " +
        "and relays to the running game. Press Play to start the client and the game.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ──────────────────────────────────────────────────────────
    public string? InstalledVersion => null;
    public string? AvailableVersion => null;

    public bool IsInstalled => ResolveWargroove2RootDir() != null;
    public bool IsRunning   { get; private set; }

    public string GameDirectory => ResolveWargroove2RootDir() ?? string.Empty;

    // ── Paths ──────────────────────────────────────────────────────────────────

    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "wargroove2_launcher.json");

    // ── Internal override state ────────────────────────────────────────────────
    private string? _overrideRootDir;
    private string? _overrideApDir;

    private Process? _gameProcess;
    private Process? _clientProcess;

    // ── AP bridge events ───────────────────────────────────────────────────────
    // External client owns the AP slot. Events exist for interface compatibility only.
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
    public event Action<int>?    GameExited;
#pragma warning restore CS0067

    // ── Constructor ────────────────────────────────────────────────────────────

    public Wargroove2Plugin()
    {
        try
        {
            var s = LoadSettings();
            if (!string.IsNullOrWhiteSpace(s.RootDirOverride) && Directory.Exists(s.RootDirOverride))
                _overrideRootDir = s.RootDirOverride;
            if (!string.IsNullOrWhiteSpace(s.ApInstallOverride) && Directory.Exists(s.ApInstallOverride))
                _overrideApDir = s.ApInstallOverride;
        }
        catch { /* fall back to detection only */ }
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    public Task CheckForUpdateAsync(CancellationToken ct = default)
        => Task.CompletedTask;

    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((10, "Checking for your Steam copy of Wargroove 2..."));

        string? steamDir = ResolveWargroove2RootDir();
        string? client   = ResolveBundledClient();

        var sb = new StringBuilder();
        if (steamDir != null)
            sb.Append("Wargroove 2 detected at \"").Append(steamDir).Append("\". ");
        else
            sb.Append("Wargroove 2 not detected via Steam. Install it (appid ")
              .Append(SteamAppId).Append("), or set the folder in Settings. ");

        sb.Append("To play: install the Wargroove 2 apworld from the FlySniper/Archipelago fork, ");
        sb.Append("start the Wargroove 2 Client (press Play here, or from the Archipelago Launcher), ");
        sb.Append("connect (enter server + username in the client), then start Wargroove 2.");

        if (client != null)
        {
            progress.Report((60, "Starting the Wargroove 2 Client..."));
            try { StartBundledClient(client); } catch { }
        }

        progress.Report((100, sb.ToString()));
        return Task.CompletedTask;
    }

    public Task<bool> VerifyInstallAsync(CancellationToken ct = default)
        => Task.FromResult(IsInstalled);

    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist.";
        if (LooksLikeWargroove2Root(folder)) return null;
        return "That does not look like a Wargroove 2 installation (Wargroove2.exe not found).";
    }

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        _ = session;
        string? client = ResolveBundledClient();
        if (client != null) try { StartBundledClient(client); } catch { }
        try { StartWargroove2(); } catch { }
        return Task.CompletedTask;
    }

    public bool SupportsStandalone => true;
    public bool ConnectsItself     => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartWargroove2();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); }   catch { }
        try { _clientProcess?.Kill(entireProcessTree: true); } catch { }
        _gameProcess   = null;
        _clientProcess = null;
        IsRunning      = false;
        return Task.CompletedTask;
    }

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Settings UI ────────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD0, 0xA0, 0x40));

        var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "Wargroove 2's Archipelago support is a community apworld (FlySniper/Archipelago fork). " +
                "You bring your own Steam copy of Wargroove 2; the Archipelago client manages the " +
                "connection and relays to the running game. You connect (server + username) inside " +
                "the client, not here.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        panel.Children.Add(SectionHeader("WARGROOVE 2 INSTALL", muted));

        string? rootDir = ResolveWargroove2RootDir();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = rootDir != null
                ? "✓ Detected (appid " + SteamAppId + "):\n" + rootDir
                : "Not detected via Steam. Install Wargroove 2 on Steam (appid " +
                  SteamAppId + "), or set the folder below.",
            FontSize = 11, Foreground = rootDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var rootRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var rootBox = new System.Windows.Controls.TextBox
        {
            Text = _overrideRootDir ?? rootDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var rootBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120, Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        rootBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Wargroove 2 install folder (contains Wargroove2.exe)",
                InitialDirectory = Directory.Exists(_overrideRootDir ?? rootDir ?? "")
                                   ? (_overrideRootDir ?? rootDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Wargroove 2 folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                _overrideRootDir = picked;
                rootBox.Text     = picked;
                SaveRootDirOverride(picked);
            }
        };
        System.Windows.Controls.DockPanel.SetDock(rootBtn, System.Windows.Controls.Dock.Right);
        rootRow.Children.Add(rootBtn);
        rootRow.Children.Add(rootBox);
        panel.Children.Add(rootRow);

        panel.Children.Add(SectionHeader("SETUP & CONNECTION", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "1) Own and install Wargroove 2 on Steam (appid " + SteamAppId + ").\n" +
                "2) Install the Wargroove 2 apworld from the FlySniper/Archipelago fork " +
                "into your Archipelago worlds/ folder.\n" +
                "3) Press Play (or start the Wargroove 2 Client from the Archipelago Launcher).\n" +
                "4) In the client, connect to your AP server and enter your slot name.\n" +
                "5) Start Wargroove 2 and begin your campaign.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        panel.Children.Add(SectionHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("Wargroove 2 on Steam ↗",          SteamStoreUrl),
            ("FlySniper/Archipelago Releases ↗", ForkReleasesUrl),
            ("Archipelago Official ↗",            ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new System.Windows.Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0), FontSize = 12,
                Margin = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
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

    private static System.Windows.Controls.TextBlock SectionHeader(
        string text, System.Windows.Media.Brush muted)
        => new()
        {
            Text = text, FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 8, 0, 8),
        };

    // ── News feed ──────────────────────────────────────────────────────────────
    public Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
        => Task.FromResult(Array.Empty<NewsItem>());

    // ── Private helpers — Wargroove 2 ROOT detection ───────────────────────────

    private string? ResolveWargroove2RootDir()
    {
        if (!string.IsNullOrWhiteSpace(_overrideRootDir) && LooksLikeWargroove2Root(_overrideRootDir))
            return _overrideRootDir;
        try { return DetectSteamWargroove2Dir(); } catch { return null; }
    }

    private static bool LooksLikeWargroove2Root(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, PreferredGameExeName));
        }
        catch { return false; }
    }

    private static string? DetectSteamWargroove2Dir()
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
                        if (LooksLikeWargroove2Root(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeWargroove2Root(conventional)) return conventional;
                }
                catch { }
            }
        }
        return null;
    }

    // ── Private helpers — Archipelago client (READ-ONLY) ──────────────────────

    private string? ResolveArchipelagoRoot()
    {
        if (!string.IsNullOrWhiteSpace(_overrideApDir) && Directory.Exists(_overrideApDir))
            return _overrideApDir;
        try
        {
            if (Directory.Exists(DefaultArchipelagoRoot)) return DefaultArchipelagoRoot;
            foreach (string root in EnumerateArchipelagoCandidates())
                if (Directory.Exists(root)) return root;
        }
        catch { }
        return null;
    }

    private static IEnumerable<string> EnumerateArchipelagoCandidates()
    {
        foreach (var sf in new[]
        {
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
        })
        {
            string? b = null;
            try { b = Environment.GetFolderPath(sf); } catch { }
            if (!string.IsNullOrWhiteSpace(b)) yield return Path.Combine(b, "Archipelago");
        }
    }

    private string? ResolveBundledClient()
    {
        try
        {
            string? root = ResolveArchipelagoRoot();
            if (root == null || !Directory.Exists(root)) return null;

            // Prefer a dedicated "wargroove 2 client" exe
            string? wg2Client = FindExe(root,
                name => name.Contains("wargroove") && name.Contains("2") && name.Contains("client"));
            if (wg2Client != null) return wg2Client;

            // Fallback: generic wargroove client
            string? wgClient = FindExe(root,
                name => name.Contains("wargroove") && name.Contains("client"));
            if (wgClient != null) return wgClient;

            string launcher = Path.Combine(root, ArchipelagoLauncherExe);
            if (File.Exists(launcher)) return launcher;

            return FindExe(root,
                name => name.Contains("archipelagolauncher") ||
                        (name.Contains("archipelago") && name.Contains("launcher")));
        }
        catch { return null; }
    }

    private static bool IsGenericApLauncher(string exePath)
    {
        string name = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
        return name.Contains("launcher") && !name.Contains("wargroove");
    }

    private static string? FindExe(string dir, Func<string, bool> predicate)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            foreach (string exe in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (predicate(name)) return exe;
            }
        }
        catch { }
        return null;
    }

    // ── Private helpers — launch ───────────────────────────────────────────────

    private void StartBundledClient(string clientExe)
    {
        var psi = new ProcessStartInfo
        {
            FileName         = clientExe,
            WorkingDirectory = Path.GetDirectoryName(clientExe) ?? "",
            UseShellExecute  = false,
        };
        if (IsGenericApLauncher(clientExe))
            psi.Arguments = Quote(Wargroove2ClientName);

        var proc = Process.Start(psi);
        if (proc == null) return;
        _clientProcess = proc;
        IsRunning = true;
        try
        {
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) =>
            {
                if (_gameProcess == null || _gameProcess.HasExited)
                    IsRunning = false;
            };
        }
        catch { }
    }

    private void StartWargroove2()
    {
        string? root = ResolveWargroove2RootDir();
        string? exe  = root != null ? Path.Combine(root, PreferredGameExeName) : null;

        if (exe != null && File.Exists(exe))
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
                catch { }
            }
            return;
        }

        if (SteamIsInstalled())
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true;
                return;
            }
            catch { }
        }

        throw new FileNotFoundException(
            "Could not find Wargroove2.exe. Open Settings and pick your Wargroove 2 " +
            "install folder, or install Wargroove 2 via Steam (appid " + SteamAppId + ").",
            PreferredGameExeName);
    }

    private static bool SteamIsInstalled()
    {
        foreach (string r in SteamRoots())
            try { if (!string.IsNullOrWhiteSpace(r) && Directory.Exists(r)) return true; }
            catch { }
        return false;
    }

    private static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        bool needs = value.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0;
        return needs ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;
    }

    // ── Steam detection helpers ────────────────────────────────────────────────

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
        try { text = File.ReadAllText(vdf); } catch { yield break; }

        foreach (string path in ExtractVdfPaths(text))
        {
            string norm = path.Replace('/', '\\').TrimEnd('\\');
            if (norm.Length > 0 && seen.Add(norm)) yield return norm;
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

    // ── Settings sidecar ───────────────────────────────────────────────────────

    private sealed class Wargroove2Settings
    {
        public string? RootDirOverride   { get; set; }
        public string? ApInstallOverride { get; set; }
    }

    private Wargroove2Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<Wargroove2Settings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(Wargroove2Settings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { }
    }

    private void SaveRootDirOverride(string dir)
    {
        var s = LoadSettings();
        s.RootDirOverride = dir;
        SaveSettings(s);
    }
}
