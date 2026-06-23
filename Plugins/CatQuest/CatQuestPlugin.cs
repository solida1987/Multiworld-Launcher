using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core;

namespace LauncherV2.Plugins.CatQuest;

// ═══════════════════════════════════════════════════════════════════════════════
// CatQuestPlugin — install / launch for "Cat Quest" (The Gentlebros, 2017),
// played through the Archipelago integration by Nikkilites. This is a NATIVE
// "ConnectsItself" integration — the in-game AP client bundled with the mod
// owns the Archipelago slot connection.
//
// ── VERIFIED FACTS (source: Nikkilites/Archipelago-CatQuest) ─────────────────
//
//   * BASE GAME: Cat Quest on Steam (appid 512900). The launcher detects the
//     Steam install via the registry + libraryfolders.vdf. A manual folder
//     picker is offered as an override and persisted in a self-contained sidecar.
//
//   * AP GAME STRING: "Cat Quest" (inferred from repo Nikkilites/Archipelago-CatQuest).
//     GameId = "cat_quest".
//
//   * HOW IT CONNECTS: the AP mod for Cat Quest uses ConnectsItself — the
//     integrated client in the mod handles the AP server connection. The user
//     launches the game and enters connection details in-game or via a config
//     file supplied by the mod. This launcher launches the game after detecting
//     the install; the mod handles the AP slot.
//
//   * ConnectsItself = true: the mod client owns the slot. The launcher must NOT
//     hold its own ApClient on this slot while the game is running.
//
//   * SupportsStandalone = true: Cat Quest runs fine without AP.
//
// ── WHAT THIS PLUGIN DOES ─────────────────────────────────────────────────────
//   1. Detect the Steam Cat Quest install via registry + appmanifest_512900.acf.
//      Manual override persisted in Games/ROMs/cat_quest/cat_quest_launcher.json.
//   2. Install/update: no automated mod download is implemented (the apworld ships
//      as a standalone .apworld; users install the client mod manually per the
//      Nikkilites/Archipelago-CatQuest README). The Settings panel links to the
//      GitHub release page.
//   3. Launch: start CatQuest.exe from the detected install, or fall back to
//      steam://rungameid/512900.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class CatQuestPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const int    STEAM_APPID    = 512900;
    private const string MOD_OWNER     = "Nikkilites";
    private const string MOD_REPO      = "Archipelago-CatQuest";
    private const string ModRepoUrl    = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── Sidecar ───────────────────────────────────────────────────────────────

    private sealed class Sidecar
    {
        public string? GameDirectoryOverride { get; set; }
    }

    private string SidecarPath
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", "cat_quest",
                        "cat_quest_launcher.json");

    private Sidecar LoadSidecar()
    {
        try
        {
            if (File.Exists(SidecarPath))
            {
                string txt = File.ReadAllText(SidecarPath);
                return JsonSerializer.Deserialize<Sidecar>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSidecar(Sidecar s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SidecarPath)!);
            File.WriteAllText(SidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { }
    }

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "cat_quest";
    public string DisplayName => "Cat Quest";
    public string Subtitle    => "Native PC · built-in AP client";
    public string ApWorldName => "Cat Quest";

    public string IconPath
        => Path.Combine(AppContext.BaseDirectory, "Assets", "cat_quest.png");

    public string ThemeAccentColor => "#F4A03C";   // Cat Quest warm orange

    public string[] GameBadges => new[] { "Requires Steam" };

    public string Description =>
        "Cat Quest is The Gentlebros' 2017 open-world action RPG where you play as " +
        "a cat hero on a quest to save your sister and defeat the dark lord Drakoth. " +
        "Explore the kingdom of Felingard, level up, learn spells, and equip gear " +
        "found throughout the overworld and dungeons. In the Archipelago randomizer " +
        "the game's spells, equipment, and dungeon clears join the multiworld pool. " +
        "Requires a Steam copy of Cat Quest.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    public bool IsInstalled => !string.IsNullOrEmpty(GameDirectory) &&
                               Directory.Exists(GameDirectory);

    public bool IsRunning { get; private set; }

    public bool ConnectsItself    => true;
    public bool SupportsStandalone => true;

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory
    {
        get
        {
            var sidecar = LoadSidecar();
            if (!string.IsNullOrEmpty(sidecar.GameDirectoryOverride) &&
                Directory.Exists(sidecar.GameDirectoryOverride))
                return sidecar.GameDirectoryOverride;
            return SteamLocator.FindGameDir(STEAM_APPID) ?? string.Empty;
        }
        set
        {
            var sidecar = LoadSidecar();
            sidecar.GameDirectoryOverride = value;
            SaveSidecar(sidecar);
        }
    }

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The Cat Quest AP mod connects directly to the AP server; the launcher
    // only tracks process lifetime.
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
    public event Action<int>?    GameExited;
#pragma warning restore CS0067

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        InstalledVersion = IsInstalled ? "installed" : null;

        try
        {
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(MOD_OWNER, MOD_REPO, ct));
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // The Cat Quest Archipelago mod is distributed as a .apworld drop-in and
        // a client mod that the user installs manually per the README. The launcher
        // guides the user to the GitHub release page rather than attempting to
        // replicate a manual install that involves game-side file placement.
        progress.Report((100,
            "Cat Quest requires manual mod installation. See the Settings panel for " +
            "setup steps and links to the Nikkilites/Archipelago-CatQuest release page."));
        return Task.CompletedTask;
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
        _ = session; // connection is entered in-game by the mod
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
        IsRunning = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (mod speaks directly to AP server) ─────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var ok      = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x00));
        var linkClr = new SolidColorBrush(Color.FromRgb(0x60, 0x9A, 0xFF));

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Section: Game install directory ───────────────────────────────
        panel.Children.Add(SectionHeader("CAT QUEST GAME DIRECTORY", muted));

        string gameDir   = GameDirectory;
        bool   gameFound = !string.IsNullOrEmpty(gameDir) && Directory.Exists(gameDir);

        panel.Children.Add(new TextBlock
        {
            Text = gameFound
                ? "Cat Quest detected: " + gameDir
                : "Cat Quest not found. Browse to the Steam install folder, or install via Steam.",
            FontSize = 11,
            Foreground = gameFound ? ok : warn,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        });

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var dirBox = new TextBox
        {
            Text = gameDir, IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var browseBtn = new Button
        {
            Content = "Browse...", Width = 90,
            Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        browseBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select Cat Quest game folder (contains CatQuest.exe)",
                InitialDirectory = gameFound ? gameDir : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                GameDirectory = dlg.FolderName;
                dirBox.Text   = dlg.FolderName;
            }
        };
        DockPanel.SetDock(browseBtn, Dock.Right);
        dirRow.Children.Add(browseBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        // ── Section: Setup guide ──────────────────────────────────────────
        panel.Children.Add(SectionHeader("SETUP GUIDE", muted));
        foreach (string step in new[]
        {
            "1. Own Cat Quest on Steam (appid 512900). Install it if you have not.",
            "2. Download the Cat Quest Archipelago mod from the GitHub link below " +
                "(Nikkilites/Archipelago-CatQuest).",
            "3. Follow the installation instructions in the repository README to " +
                "place the mod files into your Cat Quest installation.",
            "4. Drop the .apworld file into your Archipelago worlds or custom_worlds " +
                "folder, then generate a multiworld seed.",
            "5. Launch Cat Quest from the Play tab. The mod handles the AP connection " +
                "in-game — enter your server, slot, and password as prompted.",
        })
        {
            panel.Children.Add(new TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5),
            });
        }

        // ── Section: Links ────────────────────────────────────────────────
        panel.Children.Add(SectionHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("Archipelago-CatQuest (GitHub) ↗",     ModRepoUrl),
            ("Cat Quest releases ↗",                ModRepoUrl + "/releases"),
            ("Cat Quest on Steam ↗",                "https://store.steampowered.com/app/512900/Cat_Quest/"),
            ("Archipelago Official ↗",              "https://archipelago.gg"),
        })
        {
            var btn = new Button
            {
                Content = label,
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 4),
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
            string json = await _http.GetStringAsync(GH_RELEASES_URL, ct);
            using var doc  = JsonDocument.Parse(json);
            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : "",
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: el.TryGetProperty("tag_name", out var t) ? t.GetString()  ?? "" : "",
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var u) ? u.GetString()       : null
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void StartGame()
    {
        IsRunning = true;

        string gameDir = GameDirectory;
        string? exe = null;

        if (!string.IsNullOrEmpty(gameDir) && Directory.Exists(gameDir))
        {
            foreach (string name in new[] { "CatQuest.exe", "catquest.exe", "Cat Quest.exe" })
            {
                string candidate = Path.Combine(gameDir, name);
                if (File.Exists(candidate)) { exe = candidate; break; }
            }

            if (exe == null)
            {
                try
                {
                    foreach (string f in Directory.EnumerateFiles(gameDir, "*.exe",
                                 System.IO.SearchOption.TopDirectoryOnly))
                    {
                        if (Path.GetFileNameWithoutExtension(f)
                                .IndexOf("cat", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            exe = f;
                            break;
                        }
                    }
                }
                catch { }
            }
        }

        ProcessStartInfo psi;
        if (exe != null)
        {
            psi = new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute  = false,
            };
        }
        else
        {
            psi = new ProcessStartInfo(
                $"steam://rungameid/{STEAM_APPID}")
            { UseShellExecute = true };
        }

        try
        {
            var proc = Process.Start(psi);
            _gameProcess = proc;
            if (proc != null)
            {
                proc.EnableRaisingEvents = true;
                proc.Exited += (_, _) =>
                {
                    IsRunning = false;
                    GameExited?.Invoke(proc.ExitCode);
                };
            }
            else
            {
                IsRunning = false;
                GameExited?.Invoke(0);
            }
        }
        catch (Exception ex)
        {
            IsRunning = false;
            throw new InvalidOperationException(
                "Could not launch Cat Quest. Make sure the game is installed via Steam.", ex);
        }
    }

    private static TextBlock SectionHeader(string text, Brush muted) => new()
    {
        Text = text, FontSize = 10,
        FontWeight = FontWeights.SemiBold,
        Foreground = muted,
        Margin = new Thickness(0, 8, 0, 8),
    };
}
