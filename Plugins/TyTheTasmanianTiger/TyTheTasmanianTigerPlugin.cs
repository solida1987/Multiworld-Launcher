using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using LauncherV2.Core;

namespace LauncherV2.Plugins.TyTheTasmanianTiger;

// ═══════════════════════════════════════════════════════════════════════════════
// TyTheTasmanianTigerPlugin — install / launch support for
// "Ty the Tasmanian Tiger" (Krome Studios, 2002 / PC port 2016, Steam 411960)
// played through the Ty1AP-Client TygerFramework plugin by xMcacutt, which
// provides a native in-game Archipelago Multiworld client.
//
// ── REALITY CHECK (2026-06-15, sources verified) ─────────────────────────────
//
//   AP WORLD:  ty_the_tasmanian_tiger.apworld
//     Maintained by xMcacutt-Archipelago in a fork of Archipelago.
//     Latest release: v1.5.0 (2026-05-18).
//     AP game name (confirmed from worlds/ty_the_tasmanian_tiger/__init__.py):
//       "Ty the Tasmanian Tiger"
//     Repo: github.com/xMcacutt-Archipelago/Archipelago-TyTheTasmanianTiger
//
//   IN-GAME CLIENT: Ty1AP-Client (TygerFramework plugin)
//     Maintained by xMcacutt.
//     Release asset: Ty1AP-Client.zip (contains Ty1AP-Client.dll).
//     Also requires: TygerFramework (by ElusiveFluffy, v1.1.3+) and
//                    TygerMemory (by xMcacutt, v1.0.3+).
//     Repo: github.com/xMcacutt-Archipelago/Ty1AP-Client
//     The zip extracts to a folder structure; the DLL goes into the
//     "plugins" subdirectory under TygerFramework's root in the game folder.
//     The game also needs Patch_PC.rkv (shipped in the same zip).
//
//   CONNECTOR PATTERN: ConnectsItself = true
//     Once connected through the in-game TygerFramework overlay (F1 to toggle
//     the TygerFramework window, then click the AP logo in the top-right to
//     open the AP connection panel), the Ty1AP-Client plugin owns the slot on
//     the AP server. The launcher must NOT hold its own ApClient on the same
//     slot. The settings panel surfaces the session's host:port, slot name,
//     and password for the user to copy into the in-game fields.
//
//   STEAM FACTS (verified via Steam/SteamUnlocked/community posts):
//     AppID:  411960
//     Common folder name:  "TY the Tasmanian Tiger"   (TY capitalised by Steam)
//     Main exe:  TY.exe   (in the game root, same folder as TygerFramework.dll)
//
//   INSTALL STRATEGY (this plugin):
//     1. Detect existing Steam install via registry.
//     2. Allow the user to manually point at their game folder.
//     3. Download and extract Ty1AP-Client.zip from the latest GitHub release.
//        The zip is extracted into the game's root folder; TygerFramework must
//        be installed separately by the user (or via the Ty Mod Manager).
//     4. Presence of Ty1AP-Client.dll anywhere under <game> signals "installed".
//
// ── WHAT THIS PLUGIN DOES ────────────────────────────────────────────────────
//   • Detect Steam install; let the user override via folder picker.
//   • Download Ty1AP-Client.zip from the latest GitHub release and extract it
//     into the game directory (installs the AP client plugin).
//   • Verify install: game exe + Ty1AP-Client.dll both present.
//   • Launch: start TY.exe (TygerFramework hooks in automatically when the game
//     starts — the user connects via the in-game overlay).
//   • Settings panel: surfaces the AP session credentials for copy-paste,
//     plus links to TygerFramework + TygerMemory setup guides.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class TyTheTasmanianTigerPlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    /// Steam AppID for TY the Tasmanian Tiger (verified: store.steampowered.com/app/411960).
    private const string SteamAppId = "411960";

    /// Steam common folder name (Steam stores it with "TY" capitalised).
    private const string SteamCommonName = "TY the Tasmanian Tiger";

    /// Main game executable name (in the game root folder).
    private const string GameExeName = "TY.exe";

    /// DLL name dropped by the Ty1AP-Client zip into the game root / TygerFramework
    /// plugins subdirectory. We search the whole game tree to be robust against
    /// minor folder changes between releases.
    private const string ClientDllName = "Ty1AP-Client.dll";

    /// Sidecar JSON that persists the manual install-dir override for this plugin.
    private const string SidecarFileName = "ty_the_tasmanian_tiger_launcher.json";

    /// GitHub releases API endpoint for the Ty1AP-Client plugin.
    private const string ClientReleasesApi =
        "https://api.github.com/repos/xMcacutt-Archipelago/Ty1AP-Client/releases/latest";

    /// GitHub releases HTML for changelog links.
    private const string ClientReleasesHtml =
        "https://github.com/xMcacutt-Archipelago/Ty1AP-Client/releases";

    /// GitHub releases API endpoint for the apworld.
    private const string ApWorldReleasesApi =
        "https://api.github.com/repos/xMcacutt-Archipelago/Archipelago-TyTheTasmanianTiger/releases/latest";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } },
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "ty_the_tasmanian_tiger";
    public string DisplayName => "Ty the Tasmanian Tiger";
    public string Subtitle    => "Collectathon Multiworld";

    /// AP game name exactly as declared in worlds/ty_the_tasmanian_tiger/__init__.py:
    ///   game: str = "Ty the Tasmanian Tiger"
    public string ApWorldName => "Ty the Tasmanian Tiger";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "ty_the_tasmanian_tiger.png");

    /// Warm orange-brown reflecting the Australian-outback Ty aesthetic.
    public string ThemeAccentColor => "#B05A10";

    public string[] GameBadges => new[] { "Requires Steam", "ConnectsItself" };

    public string Description =>
        "Ty the Tasmanian Tiger is a 3D platformer collectathon developed by " +
        "Australian studio Krome Studios, originally released on PS2/GameCube/Xbox " +
        "in 2002 and remastered for PC (Steam, AppID 411960) in 2016. Play as Ty " +
        "and collect Thunder Eggs, Golden Cogs, and Bilbies across the Australian " +
        "outback to defeat Boss Cass and rescue your family from The Dreaming.\n\n" +
        "The Archipelago integration uses the Ty1AP-Client TygerFramework plugin " +
        "by xMcacutt. TygerFramework injects itself into TY.exe at launch; the AP " +
        "client is then accessible via an overlay (F1 to open TygerFramework, then " +
        "click the AP logo). Checks include Thunder Eggs, Golden Cogs, Bilbies, " +
        "Rangs, Talismans, and optional Picture Frames / Signposts / Extra Lives " +
        "(framesanity / signsanity / lifesanity). Level shuffle and Mul-Ty-Link " +
        "(co-op koalas in the same slot) are also supported.\n\n" +
        "This plugin downloads the Ty1AP-Client zip and extracts it into your game " +
        "folder. TygerFramework (v1.1.3+) and TygerMemory (v1.0.3+) must be " +
        "installed separately — see the links in the Settings tab.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    /// The installed client-plugin version read from the latest release tag that
    /// was downloaded. Persisted in the sidecar JSON.
    public string? InstalledVersion { get; private set; }

    /// The latest available release tag fetched from GitHub.
    public string? AvailableVersion { get; private set; }

    public bool IsInstalled =>
        !string.IsNullOrEmpty(GameDirectory) &&
        File.Exists(Path.Combine(GameDirectory, GameExeName)) &&
        FindClientDll(GameDirectory) != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Resolved game directory (Steam-detected or user-overridden).
    public string GameDirectory { get; private set; } = "";

    private string SidecarPath =>
        Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId, SidecarFileName);

    // ── Internal state ────────────────────────────────────────────────────────

    private Process?  _gameProcess;
    private Settings? _settings;

    // The AP session stored during LaunchAsync for the settings panel copy fields.
    private ApSession? _pendingSession;

    // ── AP bridge events ─────────────────────────────────────────────────────
    // ConnectsItself = true: the in-game Ty1AP-Client owns the AP slot.
    // These events are never fired by this plugin; the pragma suppresses CS0067.
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
    public event Action<int>?    GameExited;
#pragma warning restore CS0067

    // ── ConnectsItself ────────────────────────────────────────────────────────

    /// True: the Ty1AP-Client DLL (loaded by TygerFramework inside TY.exe)
    /// connects directly to the AP server. The launcher must NOT hold its own
    /// ApClient on this slot while the game is running.
    public bool ConnectsItself => true;

    /// The user can also launch Ty standalone (without an AP session) — the
    /// game runs fine without TygerFramework, and TygerFramework will simply
    /// show "not connected" if no AP session has been established.
    public bool SupportsStandalone => true;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        LoadSettings();
        RefreshGameDirectory();

        // Refresh installed version from sidecar.
        InstalledVersion = _settings?.InstalledVersion;

        // Poll GitHub for the latest Ty1AP-Client release.
        try
        {
            string json = await _http.GetStringAsync(ClientReleasesApi, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("tag_name", out var tag))
                AvailableVersion = tag.GetString();
        }
        catch
        {
            AvailableVersion = null;
        }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    /// Download Ty1AP-Client.zip from the latest GitHub release and extract it
    /// into the game directory. The user must have TY the Tasmanian Tiger
    /// installed via Steam (or manually) before calling this.
    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        LoadSettings();
        RefreshGameDirectory();

        if (string.IsNullOrEmpty(GameDirectory) ||
            !File.Exists(Path.Combine(GameDirectory, GameExeName)))
        {
            progress.Report((0,
                "TY the Tasmanian Tiger is not detected. Install it through Steam " +
                "and set the game folder in the Settings tab before installing " +
                "the AP client."));
            return;
        }

        progress.Report((5, "Fetching latest Ty1AP-Client release info..."));

        // ── Step 1: resolve the download URL from the GitHub releases API ─────
        string? downloadUrl = null;
        string? releaseTag  = null;
        try
        {
            string releaseJson = await _http.GetStringAsync(ClientReleasesApi, ct);
            using var doc = JsonDocument.Parse(releaseJson);
            releaseTag = doc.RootElement.TryGetProperty("tag_name", out var tag)
                ? tag.GetString() : null;

            if (doc.RootElement.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string? name = asset.TryGetProperty("name", out var n)
                        ? n.GetString() : null;
                    string? url = asset.TryGetProperty("browser_download_url", out var u)
                        ? u.GetString() : null;
                    if (name != null && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                        && url != null)
                    {
                        downloadUrl = url;
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            progress.Report((0, $"Failed to fetch release info: {ex.Message}"));
            return;
        }

        if (downloadUrl == null)
        {
            progress.Report((0,
                "Could not locate Ty1AP-Client.zip in the latest GitHub release. " +
                "Download it manually from:\n" + ClientReleasesHtml));
            return;
        }

        progress.Report((10, $"Downloading Ty1AP-Client {releaseTag ?? "latest"}..."));

        // ── Step 2: download the zip ──────────────────────────────────────────
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"Ty1AP-Client_{releaseTag ?? "latest"}.zip");
        try
        {
            byte[] data = await _http.GetByteArrayAsync(downloadUrl, ct);
            await File.WriteAllBytesAsync(tempZip, data, ct);
        }
        catch (Exception ex)
        {
            progress.Report((0, $"Download failed: {ex.Message}"));
            return;
        }

        progress.Report((70, "Extracting Ty1AP-Client into game folder..."));

        // ── Step 3: extract into the game directory ───────────────────────────
        // ZipFile.ExtractToDirectory with overwrite=true so updates work cleanly.
        try
        {
            ZipFile.ExtractToDirectory(tempZip, GameDirectory, overwriteFiles: true);
        }
        catch (Exception ex)
        {
            progress.Report((0, $"Extraction failed: {ex.Message}"));
            return;
        }
        finally
        {
            try { File.Delete(tempZip); } catch { }
        }

        progress.Report((90, "Saving version info..."));

        // ── Step 4: persist installed version ────────────────────────────────
        LoadSettings();
        _settings!.InstalledVersion = releaseTag;
        SaveSettings();
        InstalledVersion  = releaseTag;
        AvailableVersion  = releaseTag;

        progress.Report((100,
            $"Ty1AP-Client {releaseTag ?? "latest"} installed successfully.\n\n" +
            "IMPORTANT: TygerFramework (v1.1.3+) and TygerMemory (v1.0.3+) must " +
            "also be installed in the game folder — see the Settings tab for links. " +
            "Once both are present, launch the game and press F1 to open " +
            "TygerFramework, then click the AP logo to connect."));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        RefreshGameDirectory();
        return IsInstalled;
    }

    // ── Lifecycle — LaunchAsync ───────────────────────────────────────────────

    /// Launch TY.exe. TygerFramework injects into the process automatically
    /// when it finds TygerFramework.dll (or the hook exe) in the game folder.
    /// The user connects to the AP server through the in-game overlay.
    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        _pendingSession = session;
        return LaunchGameExe();
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        _pendingSession = null;
        return LaunchGameExe();
    }

    private Task LaunchGameExe()
    {
        RefreshGameDirectory();
        string exePath = Path.Combine(GameDirectory, GameExeName);
        if (!File.Exists(exePath))
            throw new FileNotFoundException(
                "TY.exe not found. Install TY the Tasmanian Tiger through Steam " +
                "and set the game folder in the Settings tab.", exePath);

        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = exePath,
            WorkingDirectory = GameDirectory,
            UseShellExecute  = false,
        }) ?? throw new InvalidOperationException(
                "Failed to start TY the Tasmanian Tiger.");

        _gameProcess = proc;
        IsRunning    = true;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            IsRunning = false;
            // GameExited is suppressed (CS0067) — ConnectsItself pattern does not
            // need to fire it for AP bridge, but we reset running state here.
        };

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        return Task.CompletedTask;
    }

    // ── AP bridge — pass-through (ConnectsItself) ─────────────────────────────

    public Task ReceiveItemsAsync(
        ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The in-game Ty1AP-Client receives items directly from the AP server.
        // The launcher does not relay items for ConnectsItself games.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // No launcher-side IPC channel — the in-game client manages its own
        // connection state and HUD indicator.
    }

    // ── Existing-install validation ───────────────────────────────────────────

    /// Called when the user manually browses to their game folder.
    /// Accept the folder if TY.exe is present at its root.
    public string? ValidateExistingInstall(string folder)
    {
        if (!File.Exists(Path.Combine(folder, GameExeName)))
            return "That folder does not appear to be a TY the Tasmanian Tiger " +
                   "install. Expected TY.exe at the root of the chosen folder.\n\n" +
                   "Make sure you select the game root (the folder that contains " +
                   "TY.exe, not a subfolder).";
        return null;
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        LoadSettings();
        RefreshGameDirectory();

        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var accent  = new SolidColorBrush(Color.FromRgb(0xB0, 0x5A, 0x10));
        var warn    = new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x40));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var dark    = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20));
        var border  = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33));
        var panelBg = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30));
        var linkFg  = new SolidColorBrush(Color.FromRgb(0x60, 0x9A, 0xFF));

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── How to connect notice ────────────────────────────────────────────
        var infoBox = new Border
        {
            Background      = new SolidColorBrush(Color.FromArgb(0x28, 0xB0, 0x5A, 0x10)),
            BorderBrush     = accent,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            Padding         = new Thickness(12, 10, 12, 10),
            Margin          = new Thickness(0, 0, 0, 16),
        };
        infoBox.Child = new TextBlock
        {
            Text = "Ty the Tasmanian Tiger connects to Archipelago through its own " +
                   "in-game client (Ty1AP-Client via TygerFramework). After launching " +
                   "the game:\n\n" +
                   "  1. Press F1 to open the TygerFramework overlay.\n" +
                   "  2. Click the AP logo in the top-right to open the connection window.\n" +
                   "  3. Enter your server address, slot name, and password, then click Connect.\n" +
                   "  4. On the main menu the Load Game button will become active.\n\n" +
                   "Copy your AP session credentials from the fields below.",
            FontSize     = 12,
            Foreground   = fg,
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(infoBox);

        // ── AP session credentials (copy-paste fields) ────────────────────────
        if (_pendingSession != null)
        {
            panel.Children.Add(SectionLabel("ARCHIPELAGO SESSION CREDENTIALS", muted));

            foreach (var (label, value) in new[]
            {
                ("Server Address", _pendingSession.ServerUri),
                ("Slot Name",      _pendingSession.SlotName),
                ("Password",       string.IsNullOrEmpty(_pendingSession.Password)
                                        ? "(none)" : _pendingSession.Password),
            })
            {
                panel.Children.Add(new TextBlock
                {
                    Text       = label,
                    FontSize   = 10,
                    Foreground = muted,
                    Margin     = new Thickness(0, 4, 0, 2),
                });
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
                var valBox = new TextBox
                {
                    Text        = value,
                    IsReadOnly  = true,
                    FontSize    = 12,
                    Background  = dark,
                    Foreground  = fg,
                    BorderBrush = border,
                    Margin      = new Thickness(0, 0, 8, 0),
                };
                string copyVal = value;
                var copyBtn = new Button
                {
                    Content     = "Copy",
                    Width       = 60,
                    Padding     = new Thickness(0, 6, 0, 6),
                    Background  = panelBg,
                    Foreground  = fg,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
                };
                copyBtn.Click += (_, _) =>
                {
                    try { System.Windows.Clipboard.SetText(copyVal); } catch { }
                };
                DockPanel.SetDock(copyBtn, Dock.Right);
                row.Children.Add(copyBtn);
                row.Children.Add(valBox);
                panel.Children.Add(row);
            }
        }
        else
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Launch the game from the AP session screen to see your " +
                       "connection credentials here.",
                FontSize     = 11,
                Foreground   = muted,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 12),
            });
        }

        // ── Install status ───────────────────────────────────────────────────
        panel.Children.Add(SectionLabel("INSTALL STATUS", muted));

        bool    gameFound  = !string.IsNullOrEmpty(GameDirectory) &&
                             File.Exists(Path.Combine(GameDirectory, GameExeName));
        string? clientDll  = string.IsNullOrEmpty(GameDirectory)
                                ? null : FindClientDll(GameDirectory);
        bool    clientFound = clientDll != null;

        panel.Children.Add(StatusLine(
            gameFound
                ? $"TY.exe found: {GameDirectory}"
                : "TY the Tasmanian Tiger not detected in Steam library.",
            gameFound ? success : warn));

        panel.Children.Add(StatusLine(
            clientFound
                ? $"Ty1AP-Client installed: {clientDll}"
                : "Ty1AP-Client not found — click Install / Update to download it.",
            clientFound ? success : warn));

        // TygerFramework presence check (heuristic: look for TygerFramework.dll)
        string? tfDll = string.IsNullOrEmpty(GameDirectory)
            ? null
            : Directory.EnumerateFiles(GameDirectory, "TygerFramework.dll",
                  SearchOption.AllDirectories).FirstOrDefault();
        bool tfFound = tfDll != null;
        panel.Children.Add(StatusLine(
            tfFound
                ? $"TygerFramework found: {tfDll}"
                : "TygerFramework not detected — install it separately (see links below).",
            tfFound ? success : warn));

        // ── Manual install-directory override ────────────────────────────────
        panel.Children.Add(SectionLabel("GAME DIRECTORY (OVERRIDE)", muted));
        panel.Children.Add(new TextBlock
        {
            Text = "Leave blank to use the auto-detected Steam path. If Steam is " +
                   "not installed or the game is in a non-default library, point " +
                   "this at the TY the Tasmanian Tiger root folder (the one " +
                   "containing TY.exe).",
            FontSize     = 11,
            Foreground   = muted,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 6),
        });

        string? detectedDir = ResolveSteamInstallDir();
        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var dirBox = new TextBox
        {
            Text        = _settings?.ManualInstallDir ?? "",
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new Thickness(0, 0, 8, 0),
            Background  = dark,
            Foreground  = fg,
            BorderBrush = border,
        };
        var browseBtn = new Button
        {
            Content     = "Browse...",
            Width       = 90,
            Padding     = new Thickness(0, 6, 0, 6),
            Background  = panelBg,
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        browseBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select TY the Tasmanian Tiger install folder",
                InitialDirectory = Directory.Exists(_settings?.ManualInstallDir ?? "")
                    ? _settings!.ManualInstallDir
                    : (Directory.Exists(detectedDir ?? "") ? detectedDir : AppContext.BaseDirectory),
            };
            if (dlg.ShowDialog() == true)
            {
                string? err = ValidateExistingInstall(dlg.FolderName);
                if (err != null)
                {
                    MessageBox.Show(err, "Invalid Folder",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                LoadSettings();
                _settings!.ManualInstallDir = dlg.FolderName;
                SaveSettings();
                GameDirectory = dlg.FolderName;
                dirBox.Text   = dlg.FolderName;
            }
        };
        DockPanel.SetDock(browseBtn, Dock.Right);
        dirRow.Children.Add(browseBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        // ── Required dependencies ─────────────────────────────────────────────
        panel.Children.Add(SectionLabel("REQUIRED DEPENDENCIES (install manually)", muted));
        panel.Children.Add(new TextBlock
        {
            Text = "The Ty1AP-Client requires TygerFramework (v1.1.3+) and " +
                   "TygerMemory (v1.0.3+). Download and extract each into your " +
                   "TY the Tasmanian Tiger game folder. Alternatively, use the " +
                   "Ty Mod Manager (recommended) which handles all dependencies " +
                   "automatically.",
            FontSize     = 11,
            Foreground   = muted,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 8),
        });

        foreach (var (label, url) in new[]
        {
            ("Ty Mod Manager (recommended) ↗",
             "https://github.com/xMcacutt/ty_mod_manager"),
            ("TygerFramework by ElusiveFluffy ↗",
             "https://github.com/ElusiveFluffy/TygerFramework"),
            ("TygerMemory by xMcacutt ↗",
             "https://github.com/xMcacutt/TygerMemory1"),
            ("Ty1AP-Client releases ↗",
             "https://github.com/xMcacutt-Archipelago/Ty1AP-Client/releases"),
            ("Ty the Tasmanian Tiger apworld ↗",
             "https://github.com/xMcacutt-Archipelago/Archipelago-TyTheTasmanianTiger/releases"),
            ("Archipelago Wiki — Ty the Tasmanian Tiger ↗",
             "https://archipelago.miraheze.org/wiki/Ty_the_Tasmanian_Tiger"),
        })
        {
            panel.Children.Add(LinkButton(label, url, linkFg));
        }

        // ── Steam link ────────────────────────────────────────────────────────
        panel.Children.Add(SectionLabel("LINKS", muted));
        panel.Children.Add(LinkButton(
            "TY the Tasmanian Tiger on Steam ↗",
            "https://store.steampowered.com/app/411960/TY_the_Tasmanian_Tiger/",
            linkFg));

        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    /// Fetch the latest releases from the Ty1AP-Client GitHub repo as news items.
    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            // Fetch up to 10 releases from the client plugin repo.
            string json = await _http.GetStringAsync(
                "https://api.github.com/repos/xMcacutt-Archipelago/Ty1AP-Client/releases?per_page=10",
                ct);
            using var doc = JsonDocument.Parse(json);
            var items = new List<NewsItem>();
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                string? tag  = rel.TryGetProperty("tag_name",     out var t) ? t.GetString()  : null;
                string? name = rel.TryGetProperty("name",         out var n) ? n.GetString()  : null;
                string? body = rel.TryGetProperty("body",         out var b) ? b.GetString()  : null;
                string? url  = rel.TryGetProperty("html_url",     out var u) ? u.GetString()  : null;
                string? date = rel.TryGetProperty("published_at", out var d) ? d.GetString()  : null;
                if (tag == null) continue;
                items.Add(new NewsItem(
                    Title:   name ?? $"Ty1AP-Client {tag}",
                    Body:    body ?? "",
                    Version: tag,
                    Date:    DateTimeOffset.TryParse(date, out var dt) ? dt : DateTimeOffset.MinValue,
                    Url:     url));
            }
            return items.ToArray();
        }
        catch
        {
            return Array.Empty<NewsItem>();
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// Update GameDirectory from: (1) manual override, (2) Steam auto-detect.
    private void RefreshGameDirectory()
    {
        LoadSettings();
        string? manual = _settings?.ManualInstallDir;
        if (!string.IsNullOrEmpty(manual) && Directory.Exists(manual))
        {
            GameDirectory = manual;
            return;
        }
        string? steam = ResolveSteamInstallDir();
        GameDirectory = steam ?? "";
    }

    /// Attempt to locate the TY the Tasmanian Tiger install via the Steam
    /// registry and libraryfolders.vdf. Returns null on any failure.
    private string? ResolveSteamInstallDir()
    {
        try
        {
            string? steamPath = ReadSteamPath();
            if (string.IsNullOrEmpty(steamPath)) return null;

            var libs = new List<string> { steamPath };
            string vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                foreach (string line in File.ReadAllLines(vdf))
                {
                    string trimmed = line.Trim();
                    if (!trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string? libPath = ExtractQuotedValue(trimmed, 1);
                    if (!string.IsNullOrEmpty(libPath)) libs.Add(libPath);
                }
            }

            foreach (string lib in libs)
            {
                string manifest = Path.Combine(lib, "steamapps",
                    $"appmanifest_{SteamAppId}.acf");
                string commonDir = Path.Combine(lib, "steamapps", "common",
                    SteamCommonName);

                if (File.Exists(manifest) && Directory.Exists(commonDir))
                    return commonDir;

                // Fallback: common dir + TY.exe present even without manifest.
                if (Directory.Exists(commonDir) &&
                    File.Exists(Path.Combine(commonDir, GameExeName)))
                    return commonDir;
            }
        }
        catch { /* registry / VDF read failure — silently return null */ }

        return null;
    }

    /// Read the Steam root path from the Windows registry (HKCU then HKLM).
    private static string? ReadSteamPath()
    {
        foreach (var (hive, sub) in new[]
        {
            (Registry.CurrentUser,   @"Software\Valve\Steam"),
            (Registry.LocalMachine,  @"SOFTWARE\WOW6432Node\Valve\Steam"),
            (Registry.LocalMachine,  @"SOFTWARE\Valve\Steam"),
        })
        {
            try
            {
                using var key = hive.OpenSubKey(sub);
                if (key?.GetValue("SteamPath")   is string p1 && !string.IsNullOrEmpty(p1)) return p1;
                if (key?.GetValue("InstallPath")  is string p2 && !string.IsNullOrEmpty(p2)) return p2;
            }
            catch { }
        }
        return null;
    }

    /// Return the path to Ty1AP-Client.dll anywhere under the given root,
    /// or null if not found.
    private static string? FindClientDll(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, ClientDllName,
                SearchOption.AllDirectories).FirstOrDefault();
        }
        catch { return null; }
    }

    /// Extract the Nth (0-based) quoted token from a VDF/ACF line.
    private static string? ExtractQuotedValue(string line, int index)
    {
        int count = 0, i = 0;
        while (i < line.Length)
        {
            if (line[i] != '"') { i++; continue; }
            int start = i + 1;
            i = start;
            while (i < line.Length && line[i] != '"') i++;
            if (count == index)
                return line[start..i].Replace("\\\\", "\\");
            count++;
            i++;
        }
        return null;
    }

    // ── Settings persistence ──────────────────────────────────────────────────

    private void LoadSettings()
    {
        if (_settings != null) return;
        try
        {
            if (File.Exists(SidecarPath))
            {
                string json = File.ReadAllText(SidecarPath);
                _settings = JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
            }
            else
            {
                _settings = new Settings();
            }
        }
        catch
        {
            _settings = new Settings();
        }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SidecarPath)!);
            File.WriteAllText(SidecarPath,
                JsonSerializer.Serialize(_settings, new JsonSerializerOptions
                {
                    WriteIndented = true,
                }));
        }
        catch { /* best effort — sidecar is a convenience */ }
    }

    private sealed class Settings
    {
        public string? ManualInstallDir { get; set; }
        public string? InstalledVersion { get; set; }
    }

    // ── WPF UI helpers ─────────────────────────────────────────────────────────

    private static TextBlock SectionLabel(string text, SolidColorBrush fg) =>
        new()
        {
            Text       = text,
            FontSize   = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = fg,
            Margin     = new Thickness(0, 8, 0, 6),
        };

    private static TextBlock StatusLine(string text, SolidColorBrush fg) =>
        new()
        {
            Text         = text,
            FontSize     = 11,
            Foreground   = fg,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 6),
        };

    private static Button LinkButton(string label, string url, SolidColorBrush fg)
    {
        var btn = new Button
        {
            Content             = label,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding             = new Thickness(0, 2, 0, 2),
            Background          = Brushes.Transparent,
            BorderThickness     = new Thickness(0),
            FontSize            = 12,
            Margin              = new Thickness(0, 0, 0, 4),
            Foreground          = fg,
            Cursor              = System.Windows.Input.Cursors.Hand,
        };
        string u = url;
        btn.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); }
            catch { }
        };
        return btn;
    }
}
