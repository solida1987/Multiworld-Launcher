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

// NOTE on type qualification (BUILD GOTCHA -- CS0104):
// This project sets BOTH <UseWPF>true</UseWPF> and <UseWindowsForms>true</UseWindowsForms>.
// WPF UI types are FULLY QUALIFIED below to avoid CS0104. Do NOT add
// `using System.Windows.Controls;` or `using System.Windows.Media;` here.

namespace LauncherV2.Plugins.SwRacer;

// =============================================================================
// SwRacerPlugin -- install / update / launch for
// "Star Wars Episode I: Racer" in Archipelago.
// Catalog entry: star_wars_episode_i_racer
//   APWorld : github.com/wcolding/SWR_apworld  (game = "Star Wars Episode I Racer")
//   Client  : github.com/wcolding/SWR_AP_Client
//   Steam   : App ID 808910 (also on GOG)
//   ConnectsItself = true: the SWR AP Client holds the slot connection directly.
// =============================================================================

public sealed class SwRacerPlugin : IGamePlugin
{
    private const string APWORLD_GH_OWNER = "wcolding";
    private const string APWORLD_GH_REPO  = "SWR_apworld";
    private const string CLIENT_GH_OWNER  = "wcolding";
    private const string CLIENT_GH_REPO   = "SWR_AP_Client";

    private static readonly string ApWorldRepoUrl  = "https://github.com/" + APWORLD_GH_OWNER + "/" + APWORLD_GH_REPO;
    private static readonly string ClientRepoUrl   = "https://github.com/" + CLIENT_GH_OWNER  + "/" + CLIENT_GH_REPO;
    private static readonly string GhClientRelUrl  = "https://api.github.com/repos/" + CLIENT_GH_OWNER  + "/" + CLIENT_GH_REPO  + "/releases";
    private static readonly string GhApWorldRelUrl = "https://api.github.com/repos/" + APWORLD_GH_OWNER + "/" + APWORLD_GH_REPO + "/releases";

    private const string FallbackClientVersion = "1.0.0";
    private const uint   SteamAppId            = 808910;
    private const string SteamRegKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 808910";
    private const string VersionFileName  = "swr_ap_client_version.dat";
    private const string SettingsFileName = "swr_launcher.json";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    public string GameId      => "star_wars_episode_i_racer";
    public string DisplayName => "Star Wars Episode I: Racer";
    public string Subtitle    => "Community AP Client";
    public string ApWorldName => "Star Wars Episode I Racer";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "star_wars_episode_i_racer.png");

    public string ThemeAccentColor => "#3A1A00";
    public string[] GameBadges     => new[] { "Requires game (Steam/GOG)" };

    public string Description =>
        "Star Wars Episode I: Racer (1999 / 2020 remaster) played as an Archipelago multiworld. " +
        "Pod parts, race rewards, characters, and track order are shuffled. " +
        "Goal: complete all 25 courses. Uses the SWR AP Client by wcolding. " +
        "You must own the game on Steam (App ID 808910) or GOG.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }
    public bool    IsInstalled      => ResolveClientExe() != null;
    public bool    IsRunning        { get; private set; }

    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "SwRacer");

    private string VersionFilePath  => Path.Combine(GameDirectory, VersionFileName);
    private string SettingsSidecar  => Path.Combine(GameDirectory, SettingsFileName);
    private string ApWorldLocalPath =>
        Path.Combine(GameDirectory, _apWorldFileName ?? "star_wars_episode_i_racer.apworld");

    private Process? _clientProcess;
    private Process? _gameProcess;
    private string?  _apWorldFileName;

#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
    public event Action<int>?    GameExited;
#pragma warning restore CS0067

    public bool ConnectsItself     => true;
    public bool SupportsStandalone => true;

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            InstalledVersion = File.Exists(VersionFilePath) && IsInstalled
                ? (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() : null;
        }
        catch { InstalledVersion = null; }
        try
        {
            var (version, _, _, _) = await ResolveLatestReleaseAsync(ct);
            AvailableVersion = version;
        }
        catch { AvailableVersion = null; }
    }

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress, CancellationToken ct = default)
    {
        progress.Report((2, "Checking latest SWR AP Client release..."));
        var (version, zipUrl, apworldUrl, apworldName) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;
        if (apworldName != null) _apWorldFileName = apworldName;

        if (IsInstalled && File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100, "SWR AP Client is up to date."));
            return;
        }

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find a Windows download. Download manually from " + ClientRepoUrl + "/releases.");

        await DownloadAndExtractClientAsync(zipUrl, version, progress, ct);

        if (apworldUrl != null)
        {
            try
            {
                progress.Report((85, "Downloading the Star Wars Episode I Racer apworld..."));
                byte[] apworld = await _http.GetByteArrayAsync(apworldUrl, ct);
                await File.WriteAllBytesAsync(ApWorldLocalPath, apworld, ct);
                progress.Report((92, Path.GetFileName(ApWorldLocalPath) + " saved in client folder."));
            }
            catch (OperationCanceledException) { throw; }
            catch { progress.Report((92, "Could not download apworld -- get it from " + ApWorldRepoUrl + "/releases.")); }
        }

        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;
        progress.Report((100, "SWR AP Client ready. Make sure your game is installed, then press Play."));
    }

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    { await Task.CompletedTask; return IsInstalled; }

    public string? ValidateExistingInstall(string folder)
    {
        foreach (string name in new[] { "SWEP1RCR.exe", "sw_ep1racer.exe", "racer.exe" })
            if (File.Exists(Path.Combine(folder, name))) return null;
        return "Could not find SWEP1RCR.exe. Pick the Star Wars Episode I: Racer install directory.";
    }

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        string clientExe = ResolveClientExe()
            ?? throw new FileNotFoundException("SWR AP Client not installed. Click Install first.",
                   Path.Combine(GameDirectory, "SWR_AP_Client.exe"));
        StartGame();
        StartClientProcess(clientExe, BuildClientArguments(session));
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    { StartGame(); return Task.CompletedTask; }

    public Task StopAsync()
    {
        try { _clientProcess?.Kill(entireProcessTree: true); } catch { }
        try { _gameProcess?.Kill(entireProcessTree: true);  } catch { }
        IsRunning = false;
        return Task.CompletedTask;
    }

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;
    public void OnApStateChanged(ApConnectionState state) { }

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var linkClr = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF));
        var panel   = new System.Windows.Controls.StackPanel
                      { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        panel.Children.Add(MakeHeader("AP CLIENT DIRECTORY", muted));
        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = GameDirectory, IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Browse...", Width = 90, Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select SWR AP Client folder",
                InitialDirectory = Directory.Exists(GameDirectory) ? GameDirectory : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true) { GameDirectory = dlg.FolderName; dirBox.Text = dlg.FolderName; }
        };
        System.Windows.Controls.DockPanel.SetDock(dirBtn, System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(dirBtn); dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = IsInstalled ? "SWR AP Client is installed" : "Not installed -- click Install in the Play tab",
            FontSize = 11, Foreground = IsInstalled ? success : muted,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        panel.Children.Add(MakeHeader("GAME INSTALL PATH", muted));
        string? detectedGameExe = DetectGameExe();
        string? savedGameDir    = LoadSettings().GameDirectory;
        string? activeGameDir   = !string.IsNullOrEmpty(savedGameDir)
            ? savedGameDir : (detectedGameExe != null ? Path.GetDirectoryName(detectedGameExe) : null);

        var gameRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var gameBox = new System.Windows.Controls.TextBox
        {
            Text = activeGameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var gameBtn = new System.Windows.Controls.Button
        {
            Content = "Browse...", Width = 90, Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        gameBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Select Star Wars Episode I: Racer install folder" };
            if (dlg.ShowDialog() == true)
            {
                string? reason = ValidateExistingInstall(dlg.FolderName);
                if (reason != null)
                {
                    System.Windows.MessageBox.Show(reason, "Wrong folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                var s = LoadSettings(); s.GameDirectory = dlg.FolderName; SaveSettings(s);
                gameBox.Text = dlg.FolderName;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(gameBtn, System.Windows.Controls.Dock.Right);
        gameRow.Children.Add(gameBtn); gameRow.Children.Add(gameBox);
        panel.Children.Add(gameRow);
        bool gameFound = detectedGameExe != null || (activeGameDir != null && Directory.Exists(activeGameDir));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = gameFound
                ? "Game found" + (detectedGameExe != null && string.IsNullOrEmpty(savedGameDir) ? " (auto-detected)" : "")
                : "Game not found -- install via Steam (App ID 808910) or GOG, or browse above.",
            FontSize = 11, Foreground = gameFound ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        if (IsInstalled && File.Exists(ApWorldLocalPath))
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = Path.GetFileName(ApWorldLocalPath) + " -- copy to Archipelago custom_worlds to generate multiworlds.",
                FontSize = 11, Foreground = muted, TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 12),
            });

        panel.Children.Add(MakeHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("SWR APWorld (GitHub) ↗",   ApWorldRepoUrl),
            ("SWR AP Client (GitHub) ↗", ClientRepoUrl),
            ("Star Wars Ep I Racer on Steam ↗", "https://store.steampowered.com/app/808910/STAR_WARS_Episode_I_Racer/"),
            ("Archipelago Wiki ↗",        "https://archipelago.miraheze.org/wiki/Star_Wars_Episode_I_Racer"),
            ("Archipelago Official ↗",   "https://archipelago.gg"),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label, HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new System.Windows.Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0), FontSize = 12,
                Margin = new System.Windows.Thickness(0, 0, 0, 4), Foreground = linkClr,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) =>
            { try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); } catch { } };
            panel.Children.Add(btn);
        }
        return panel;
    }

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        var items = new List<NewsItem>();
        try { items.AddRange(await FetchReleaseNews(GhClientRelUrl,  "SWR Client",  ct)); } catch { }
        try { items.AddRange(await FetchReleaseNews(GhApWorldRelUrl, "SWR APWorld", ct)); } catch { }
        items.Sort((a, b) => b.Date.CompareTo(a.Date));
        return items.Count > 10 ? items.GetRange(0, 10).ToArray() : items.ToArray();
    }

    private async Task<List<NewsItem>> FetchReleaseNews(
        string releasesUrl, string label, CancellationToken ct)
    {
        string json = await _http.GetStringAsync(releasesUrl, ct);
        using var doc = JsonDocument.Parse(json);
        var result = new List<NewsItem>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            DateTimeOffset date = DateTimeOffset.MinValue;
            if (el.TryGetProperty("published_at", out var d) && d.ValueKind == JsonValueKind.String)
                DateTimeOffset.TryParse(d.GetString(), out date);
            string name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            result.Add(new NewsItem(
                Title:   string.IsNullOrWhiteSpace(name) ? label : name,
                Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                Version: el.TryGetProperty("tag_name", out var t) ? NormalizeTag(t.GetString()) ?? "" : "",
                Date:    date,
                Url:     el.TryGetProperty("html_url", out var u) ? u.GetString() : null
            ));
            if (result.Count >= 10) break;
        }
        return result;
    }

    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.Length > 1 && tag[0] == 'v' && char.IsDigit(tag[1]) ? tag[1..] : tag;
    }

    private async Task<(string Version, string? ZipUrl, string? ApWorldUrl, string? ApWorldName)>
        ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GhClientRelUrl, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var rel in doc.RootElement.EnumerateArray())
                {
                    if (rel.TryGetProperty("draft", out var dr) && dr.ValueKind == JsonValueKind.True) continue;
                    string? version = rel.TryGetProperty("tag_name", out var t) ? NormalizeTag(t.GetString()) : null;
                    if (version == null) continue;
                    if (rel.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                    {
                        var (zip, apworld, apworldName) = PickWindowsAssets(assets);
                        if (zip != null) return (version, zip, apworld, apworldName);
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        string fallbackZip = "https://github.com/" + CLIENT_GH_OWNER + "/" + CLIENT_GH_REPO +
                             "/releases/download/v" + FallbackClientVersion + "/SWR_AP_Client-win64.zip";
        return (FallbackClientVersion, fallbackZip, null, null);
    }

    private static (string? Zip, string? ApWorld, string? ApWorldName) PickWindowsAssets(JsonElement assets)
    {
        string? zip = null, apworld = null, apworldName = null, anyExe = null;
        foreach (var a in assets.EnumerateArray())
        {
            string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name == null || url == null) continue;
            string lower = name.ToLowerInvariant();
            if (lower.EndsWith(".apworld")) { apworld = url; apworldName = name; continue; }
            if (lower.Contains("source") || lower.Contains("linux") || lower.Contains("ubuntu") ||
                lower.Contains("mac") || lower.Contains("darwin")) continue;
            if (lower.EndsWith(".zip"))
            {
                if (zip == null && (lower.Contains("win") || lower.Contains("x64") || lower.Contains("x86"))) zip = url;
                else anyExe ??= url;
            }
            else if (lower.EndsWith(".exe")) anyExe ??= url;
        }
        zip ??= anyExe;
        return (zip, apworld, apworldName);
    }

    private string? ResolveClientExe()
    {
        if (!Directory.Exists(GameDirectory)) return null;
        string preferred = Path.Combine(GameDirectory, "SWR_AP_Client.exe");
        if (File.Exists(preferred)) return preferred;
        try
        {
            foreach (string exe in Directory.EnumerateFiles(GameDirectory, "*.exe"))
            {
                string stem = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (stem.Contains("unins") || stem.Contains("setup")) continue;
                if (stem.Contains("swr") || stem.Contains("racer")) return exe;
            }
        }
        catch { }
        return null;
    }

    private string? DetectGameExe()
    {
        string? savedDir = LoadSettings().GameDirectory;
        if (!string.IsNullOrEmpty(savedDir)) { var f = FindGameExeIn(savedDir); if (f != null) return f; }
        try
        {
            object? loc = Microsoft.Win32.Registry.GetValue(SteamRegKey, "InstallLocation", null);
            if (loc is string dir && Directory.Exists(dir)) { var f = FindGameExeIn(dir); if (f != null) return f; }
        }
        catch { }
        string def = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam", "steamapps", "common", "STAR WARS Episode I Racer");
        if (Directory.Exists(def)) { var f = FindGameExeIn(def); if (f != null) return f; }
        return null;
    }

    private static string? FindGameExeIn(string dir)
    {
        foreach (string name in new[] { "SWEP1RCR.exe", "sw_ep1racer.exe", "racer.exe" })
        {
            string path = Path.Combine(dir, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private void StartGame()
    {
        string? gameExe = DetectGameExe();
        if (gameExe != null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = gameExe,
                WorkingDirectory = Path.GetDirectoryName(gameExe) ?? "",
                UseShellExecute  = false,
            };
            try
            {
                var proc = Process.Start(psi);
                if (proc != null)
                {
                    _gameProcess = proc;
                    proc.EnableRaisingEvents = true;
                    proc.Exited += (_, _) =>
                    {
                        try { _clientProcess?.Kill(entireProcessTree: true); } catch { }
                        IsRunning = false;
                        GameExited?.Invoke(proc.ExitCode);
                    };
                    return;
                }
            }
            catch { }
        }
        try
        {
            Process.Start(new ProcessStartInfo("steam://rungameid/" + SteamAppId) { UseShellExecute = true });
            IsRunning = true;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Could not launch Star Wars Episode I: Racer. " +
                "Install the game (Steam App ID 808910) or pick its folder in Settings.", ex);
        }
    }

    private static string BuildClientArguments(ApSession session)
    {
        var (host, port) = ParseServerHostPort(session.ServerUri);
        var sb = new System.Text.StringBuilder();
        sb.Append("--server ").Append(QuoteArg(host + ":" + port));
        sb.Append(" --name ").Append(QuoteArg(session.SlotName));
        if (!string.IsNullOrEmpty(session.Password))
            sb.Append(" --password ").Append(QuoteArg(session.Password));
        return sb.ToString();
    }

    private static string QuoteArg(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        if (value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return value;
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private void StartClientProcess(string exePath, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? GameDirectory,
            UseShellExecute  = false,
        };
        if (!string.IsNullOrEmpty(arguments)) psi.Arguments = arguments;
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start the SWR AP Client.");
        _clientProcess = proc; IsRunning = true;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            if (_gameProcess == null || _gameProcess.HasExited) { IsRunning = false; GameExited?.Invoke(proc.ExitCode); }
        };
    }

    private static (string Host, int Port) ParseServerHostPort(string serverUri)
    {
        string s = serverUri.Trim();
        foreach (string prefix in new[] { "wss://", "ws://", "archipelago://" })
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { s = s[prefix.Length..]; break; }
        int slash = s.IndexOf('/'); if (slash >= 0) s = s[..slash];
        string host = s; int port = 38281; int colonCount = 0;
        foreach (char c in s) if (c == ':') colonCount++;
        if (s.StartsWith('['))
        {
            int close = s.IndexOf(']');
            if (close > 0)
            {
                host = s[1..close];
                string rest = s[(close + 1)..];
                if (rest.StartsWith(':') && int.TryParse(rest[1..], out int p6) && p6 > 0 && p6 <= 65535) port = p6;
            }
        }
        else if (colonCount <= 1)
        {
            int colon = s.LastIndexOf(':');
            if (colon > 0 && int.TryParse(s[(colon + 1)..], out int p) && p > 0 && p <= 65535)
            { host = s[..colon]; port = p; }
        }
        if (host.Length == 0) host = "archipelago.gg";
        return (host, port);
    }

    private async Task DownloadAndExtractClientAsync(
        string downloadUrl, string version,
        IProgress<(int Pct, string Msg)> progress, CancellationToken ct)
    {
        bool isZip = downloadUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        string tempFile = Path.Combine(Path.GetTempPath(),
            "swr_apclient_" + version + "_" + Guid.NewGuid().ToString("N") + (isZip ? ".zip" : ".exe"));
        try
        {
            progress.Report((5, "Downloading SWR AP Client " + version + "..."));
            using var response = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? -1; long downloaded = 0;
            await using (var src = await response.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tempFile))
            {
                var buf = new byte[81920]; int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct); downloaded += n;
                    if (total > 0) progress.Report(((int)(5 + 60 * downloaded / total),
                        "Downloading... " + downloaded / 1024 + " KB"));
                }
                await dst.FlushAsync(ct);
            }
            progress.Report((70, "Installing SWR AP Client..."));
            Directory.CreateDirectory(GameDirectory);
            if (isZip)
            {
                System.IO.Compression.ZipFile.ExtractToDirectory(tempFile, GameDirectory, overwriteFiles: true);
                if (ResolveClientExe() == null)
                {
                    string[] subdirs = Directory.GetDirectories(GameDirectory);
                    if (subdirs.Length == 1)
                    {
                        string sub = subdirs[0];
                        foreach (string fileSrc in Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories))
                        {
                            string rel = Path.GetRelativePath(sub, fileSrc);
                            string fileDst = Path.Combine(GameDirectory, rel);
                            Directory.CreateDirectory(Path.GetDirectoryName(fileDst)!);
                            File.Move(fileSrc, fileDst, overwrite: true);
                        }
                        Directory.Delete(sub, recursive: true);
                    }
                }
            }
            else { File.Copy(tempFile, Path.Combine(GameDirectory, "SWR_AP_Client.exe"), overwrite: true); }
            progress.Report((80, "SWR AP Client extracted."));
        }
        finally { try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { } }
    }

    private sealed class SwRacerSettings { public string? GameDirectory { get; set; } }

    private SwRacerSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecar))
            {
                string txt = File.ReadAllText(SettingsSidecar);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<SwRacerSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(SwRacerSettings s)
    {
        try
        {
            Directory.CreateDirectory(GameDirectory);
            File.WriteAllText(SettingsSidecar,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new System.Text.UTF8Encoding(false));
        }
        catch { }
    }

    private static System.Windows.Controls.TextBlock MakeHeader(
        string text, System.Windows.Media.Brush color) => new()
    {
        Text = text, FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
        Foreground = color, Margin = new System.Windows.Thickness(0, 8, 0, 8),
    };
}