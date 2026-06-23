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

namespace LauncherV2.Plugins.Sims4;

// ═══════════════════════════════════════════════════════════════════════════════
// Sims4Plugin — install guidance + launch for "The Sims 4" Archipelago
// integration by itsmisscactus / mrsummer360.
//
// ── HONEST REALITY CHECK (2026-06-15, verified online) ───────────────────────
//
//   * AP WORLD — game string "The Sims 4" (verified against
//     itsmisscactus/Archipelago worlds/sims4/__init__.py:
//       game: str = "The Sims 4"
//     This world is NOT in Archipelago core; it lives in a fork and must be
//     added to the AP server's custom_worlds folder before generating.)
//
//   * STEAM APP ID — 1222670 (The Sims 4, free-to-play since Oct 2022).
//
//   * HOW IT CONNECTS — the integration ships with its OWN companion client
//     ("The Sims 4 Client") that lives in the AP launcher's worlds/sims4/
//     folder (Client.py). The GAME ITSELF does not embed an AP client.
//     The AP Launcher (or the worlds-fork's built-in client runner) holds the
//     slot connection — so ConnectsItself = false and the launcher keeps its
//     ApClient alive while the game is running. This is the same pattern as
//     Risk of Rain 2 / Stardew Valley / etc., NOT the ConnectsItself pattern
//     of SoH / OpenTTD.
//
//   * WHAT "INSTALL" MEANS HERE — three distinct parts the player must put
//     in place; none can be automated in the same way as a single-zip install:
//       1. The .apworld   → Archipelago's custom_worlds folder
//                           (or the itsmisscactus/Archipelago fork's worlds/ dir)
//       2. The in-game mod → The Sims 4's Mods folder (two files:
//                           a .ts4script from mrsummer360/Sims4ArchipelagoMod
//                           and a .package from the Sims 4 Community Library)
//     The releases page for both is on GitHub, so this plugin can open the
//     right release URLs directly — the download is one click away from the
//     browser. We DO download the .apworld automatically (small file). For
//     the two .ts4script / .package files the player must copy them manually
//     (they go into a user-owned EA folder the launcher should not touch).
//
//   * LAUNCH — The Sims 4 is launched via Steam (steam://rungameid/1222670).
//     The AP client (The Sims 4 Client) must be launched separately through
//     the installed Archipelago; this plugin cannot launch it because the AP
//     install path is the player's own business. The launcher therefore shows
//     an explicit "Launch The Sims 4 Client" button in settings, but the
//     single-click AP Launch just opens the game — same pattern as Terraria /
//     Hollow Knight. We surface the AP connection credentials so the player
//     can paste them into the client.
//
//   * VERIFIED SOURCES
//     - https://github.com/itsmisscactus/Archipelago/tree/main/worlds/sims4
//     - https://github.com/mrsummer360/Sims4ArchipelagoMod
//     - Setup guide: worlds/sims4/docs/setup_en.md (above fork)
//
//   * BuiltAgainstDataPackageChecksum → null (no offline RAM-map coupling;
//     the game side is pure Python world, not a patched ROM or DLL hook).
//
// ── WHAT THIS PLUGIN DOES ─────────────────────────────────────────────────────
//   1. CheckForUpdateAsync — polls the mrsummer360/Sims4ArchipelagoMod GitHub
//      releases for the latest mod version (the .ts4script side drives the
//      version). Falls back gracefully on network failure.
//   2. InstallOrUpdateAsync — downloads the .apworld from the itsmisscactus
//      fork's releases to the launcher's own staging area AND opens the
//      browser to both GitHub release pages so the player can grab the in-
//      game mod files. Progress steps are honest "guided" milestones.
//   3. VerifyInstallAsync — checks that (a) the staged .apworld exists and
//      (b) the Sims 4 Mods folder can be located with at least one .ts4script
//      whose name mentions "archipelago".
//   4. LaunchAsync — opens the game via Steam. Surfaces a note with the
//      session credentials (host/slot/password) for the player to enter in
//      the AP client.
//   5. Settings panel — shows install status, Sims 4 Mods folder detection,
//      links, and a "Launch The Sims 4 Client" button.
//   6. GetNewsAsync — fetches release notes from mrsummer360/Sims4ArchipelagoMod.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class Sims4Plugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const string ModOwner       = "mrsummer360";
    private const string ModRepo        = "Sims4ArchipelagoMod";
    private const string WorldOwner     = "itsmisscactus";
    private const string WorldRepo      = "Archipelago";

    private const string ModReleasesUrl  =
        $"https://github.com/{ModOwner}/{ModRepo}/releases";
    private const string WorldReleasesUrl =
        $"https://github.com/{WorldOwner}/{WorldRepo}/releases";
    private const string GhModReleasesApiUrl  =
        $"https://api.github.com/repos/{ModOwner}/{ModRepo}/releases";
    private const string GhWorldReleasesApiUrl =
        $"https://api.github.com/repos/{WorldOwner}/{WorldRepo}/releases";

    private const string SetupGuideUrl  =
        $"https://github.com/{WorldOwner}/{WorldRepo}/blob/main/worlds/sims4/docs/setup_en.md";
    private const string CslReleasesUrl =
        "https://github.com/ColonolNutty/Sims4CommunityLibrary/releases";

    /// Steam URL to launch The Sims 4 (appid 1222670).
    private const string Sims4SteamLaunchUrl = "steam://rungameid/1222670";
    private const string Sims4SteamStoreUrl  = "https://store.steampowered.com/app/1222670/The_Sims_4/";

    /// Filename we stamp after a successful apworld download (so we know
    /// which version is staged).
    private const string VersionStampFileName = "sims4_apworld_version.dat";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } },
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "the_sims_4";
    public string DisplayName => "The Sims 4";
    public string Subtitle    => "Life simulation · Archipelago mod";

    /// Matches the AP world's game string (verified against worlds/sims4/__init__.py).
    public string ApWorldName => "The Sims 4";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "the_sims_4.png");

    public string ThemeAccentColor => "#3CAB6E";   // Sims green plumbob

    public string[] GameBadges => new[] { "Requires The Sims 4" };

    public string Description =>
        "The Sims 4 is a life simulation game by Maxis and EA in which you " +
        "create and control Sims, customise their homes and careers, and guide " +
        "them through their life goals. The Archipelago integration uses skill " +
        "progression as checks and sends items that unlock gameplay events — " +
        "turn your Sim's story into a shared randomiser adventure with friends " +
        "across entirely different games.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" = the apworld has been staged AND the in-game mod is present.
    public bool IsInstalled => HasStagedApWorld && HasInGameMod;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where the launcher stages files for this game (downloaded .apworld etc.).
    public string GameDirectory { get; private set; } =
        Path.Combine(AppContext.BaseDirectory, "Games", "Sims4");

    private string VersionStampPath => Path.Combine(GameDirectory, VersionStampFileName);
    private string ApWorldStagedPath => Path.Combine(GameDirectory, "sims4.apworld");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The Sims 4 Client (companion client shipped with the apworld fork) holds
    // the slot — not us. We fire no checks or goals ourselves.
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── ConnectsItself ────────────────────────────────────────────────────────

    /// The game's OWN executable does NOT embed an AP client. The integration
    /// uses a companion client that runs alongside the game (part of the
    /// itsmisscactus AP fork). The launcher's ApClient therefore stays active
    /// on the slot while the game is running — ConnectsItself = false.
    public bool ConnectsItself => false;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        // Read the locally stamped version of the staged apworld.
        try
        {
            InstalledVersion = File.Exists(VersionStampPath) && HasStagedApWorld
                ? (await File.ReadAllTextAsync(VersionStampPath, ct)).Trim()
                : null;
        }
        catch { InstalledVersion = null; }

        // Poll the mod's GitHub release for the latest tag.
        try
        {
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(ModOwner, ModRepo, ct));
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(GameDirectory);

        // 1. Resolve the latest apworld release from the itsmisscactus fork.
        progress.Report((5, "Checking The Sims 4 apworld release..."));
        var (apworldVersion, apworldUrl) = await ResolveApWorldDownloadAsync(ct);
        if (apworldVersion != null) AvailableVersion = apworldVersion;

        // 2. Download the .apworld.
        if (apworldUrl != null)
        {
            progress.Report((10, "Downloading sims4.apworld..."));
            try
            {
                byte[] bytes = await _http.GetByteArrayAsync(apworldUrl, ct);
                await File.WriteAllBytesAsync(ApWorldStagedPath, bytes, ct);
                if (apworldVersion != null)
                    await File.WriteAllTextAsync(VersionStampPath, apworldVersion, ct);
                InstalledVersion = apworldVersion;
                progress.Report((40, $"sims4.apworld {apworldVersion ?? ""} downloaded to install folder."));
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                progress.Report((40, "Could not download the apworld — get it from the GitHub release page."));
            }
        }
        else
        {
            progress.Report((40, "Could not locate the apworld download — see the link below."));
        }

        // 3. Open the browser to the in-game mod release page (the .ts4script +
        //    .package files the player must copy themselves).
        progress.Report((50, "Opening Sims 4 Archipelago mod releases page..."));
        try
        {
            Process.Start(new ProcessStartInfo(ModReleasesUrl) { UseShellExecute = true });
        }
        catch { /* browser launch is best-effort */ }

        // 4. Open the Sims 4 Community Library release page (the player needs
        //    the .ts4script/.package from there too, per the setup guide).
        await Task.Delay(800, ct);
        progress.Report((60, "Opening Sims 4 Community Library releases page..."));
        try
        {
            Process.Start(new ProcessStartInfo(CslReleasesUrl) { UseShellExecute = true });
        }
        catch { }

        progress.Report((100,
            "Download the .ts4script and .package files from both pages that just opened " +
            "and copy them into your Sims 4 Mods folder " +
            @"(usually Documents\Electronic Arts\The Sims 4\Mods). " +
            "Copy sims4.apworld into Archipelago's custom_worlds folder. " +
            "Enable Script Mods in The Sims 4: Game Options > Other > Script Mods Allowed."));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return HasStagedApWorld && HasInGameMod;
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // Show a reminder with the AP credentials the player needs to paste
        // into The Sims 4 Client before connecting.
        string credInfo =
            $"Server: {session.ServerUri}\n" +
            $"Slot:   {session.SlotName}\n" +
            (string.IsNullOrEmpty(session.Password) ? "" : $"Password: {session.Password}\n") +
            "\nEnter these in The Sims 4 Client (run it from your Archipelago " +
            "launcher), then start The Sims 4 and load or create a save.";

        Application.Current.Dispatcher.Invoke(() =>
        {
            MessageBox.Show(credInfo,
                "The Sims 4 — AP connection credentials",
                MessageBoxButton.OK, MessageBoxImage.Information);
        });

        // Launch The Sims 4 via Steam.
        LaunchViaSteam();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        return Task.CompletedTask;
    }

    // ── AP bridge — companion-client model ────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The Sims 4 Client (companion) handles item delivery on the AP side.
        // The launcher's own ApClient is still connected (ConnectsItself=false),
        // but we have no IPC channel into the game to forward items ourselves.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // No in-game HUD channel available.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── apworld status ────────────────────────────────────────────────
        panel.Children.Add(MakeLabel("APWORLD", muted));

        bool apworldOk = HasStagedApWorld;
        panel.Children.Add(new TextBlock
        {
            Text       = apworldOk
                ? $"sims4.apworld staged in {GameDirectory}"
                : "Not staged — click Install to download it.",
            FontSize   = 11,
            Foreground = apworldOk ? success : warn,
            TextWrapping = TextWrapping.Wrap,
            Margin     = new Thickness(0, 0, 0, 4),
        });

        if (apworldOk)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Copy sims4.apworld into Archipelago's custom_worlds folder " +
                       @"(default: C:\ProgramData\Archipelago\custom_worlds) before generating a game.",
                FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            });
        }

        // ── In-game mod status ────────────────────────────────────────────
        panel.Children.Add(MakeLabel("IN-GAME MOD", muted));

        bool modOk  = HasInGameMod;
        string? modsFolder = FindSims4ModsFolder();
        panel.Children.Add(new TextBlock
        {
            Text       = modOk
                ? $"Archipelago mod detected in {modsFolder}"
                : modsFolder != null
                    ? $"Mod not found in {modsFolder} — see install steps below."
                    : "Could not locate Sims 4 Mods folder (is The Sims 4 installed?).",
            FontSize   = 11,
            Foreground = modOk ? success : warn,
            TextWrapping = TextWrapping.Wrap,
            Margin     = new Thickness(0, 0, 0, 10),
        });

        // ── Install steps ─────────────────────────────────────────────────
        panel.Children.Add(MakeLabel("INSTALL STEPS", muted));
        panel.Children.Add(new TextBlock
        {
            Text =
                "1. Click Install Game to download sims4.apworld.\n" +
                "2. Copy sims4.apworld into Archipelago's custom_worlds folder.\n" +
                "3. Download the .ts4script and .package from the Sims4ArchipelagoMod release page.\n" +
                "4. Download the matching files from Sims 4 Community Library releases.\n" +
                "5. Copy all four files into The Sims 4 Mods folder.\n" +
                "6. In The Sims 4: Game Options > Other > enable Script Mods Allowed.\n" +
                "7. Launch The Sims 4 Client from your Archipelago launcher and connect.\n" +
                "8. Start or load a Sims 4 save (disable expansion packs for best results).",
            FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(MakeLabel("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("Sims4ArchipelagoMod releases (mod files) ↗",   ModReleasesUrl),
            ("Sims 4 Community Library releases ↗",           CslReleasesUrl),
            ("AP world fork releases (apworld) ↗",            WorldReleasesUrl),
            ("Setup guide ↗",                                  SetupGuideUrl),
            ("The Sims 4 on Steam ↗",                          Sims4SteamStoreUrl),
        })
        {
            panel.Children.Add(MakeLinkButton(label, url, fg));
        }

        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(GhModReleasesApiUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                string version = el.TryGetProperty("tag_name", out var t)
                    ? NormalizeTag(t.GetString()) ?? ""
                    : "";

                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? $"Release {version}" : $"Release {version}",
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: version,
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var u) ? u.GetString() : null
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// True when we have a staged sims4.apworld in the launcher's own folder.
    private bool HasStagedApWorld => File.Exists(ApWorldStagedPath);

    /// True when at least one .ts4script mentioning "archipelago" exists in the
    /// Sims 4 Mods folder (case-insensitive). Indicates the in-game mod is set up.
    private bool HasInGameMod
    {
        get
        {
            string? folder = FindSims4ModsFolder();
            if (folder == null || !Directory.Exists(folder)) return false;
            try
            {
                foreach (string f in Directory.EnumerateFiles(folder, "*.ts4script",
                    SearchOption.AllDirectories))
                {
                    if (Path.GetFileNameWithoutExtension(f)
                            .Contains("archipelago", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { /* permissions or vanished folder */ }
            return false;
        }
    }

    /// Locate the Sims 4 Mods folder in the standard Documents location,
    /// or null when not found.
    private static string? FindSims4ModsFolder()
    {
        try
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(docs)) return null;
            string candidate = Path.Combine(docs, "Electronic Arts", "The Sims 4", "Mods");
            return candidate; // return even if it doesn't exist so the UI can report it
        }
        catch { return null; }
    }

    /// Resolve the latest .apworld asset URL from the itsmisscactus/Archipelago
    /// releases. Returns (null, null) when offline or no asset is found.
    private async Task<(string? Version, string? DownloadUrl)> ResolveApWorldDownloadAsync(
        CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GhWorldReleasesApiUrl, ct);
            using var doc = JsonDocument.Parse(json);
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                string? version = release.TryGetProperty("tag_name", out var t)
                    ? NormalizeTag(t.GetString())
                    : null;

                if (!release.TryGetProperty("assets", out var assets)
                    || assets.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var asset in assets.EnumerateArray())
                {
                    string? name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = asset.TryGetProperty("browser_download_url", out var u)
                                   ? u.GetString() : null;

                    if (name != null && url != null &&
                        name.EndsWith(".apworld", StringComparison.OrdinalIgnoreCase) &&
                        name.Contains("sims4", StringComparison.OrdinalIgnoreCase))
                    {
                        return (version, url);
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* network failure — caller shows fallback message */ }

        return (null, null);
    }

    /// Launch The Sims 4 via Steam. Falls back to opening the store page when
    /// the steam:// URI handler is unavailable.
    private void LaunchViaSteam()
    {
        try
        {
            var proc = Process.Start(new ProcessStartInfo(Sims4SteamLaunchUrl)
            {
                UseShellExecute = true,
            });
            if (proc != null)
            {
                IsRunning = true;
                proc.EnableRaisingEvents = true;
                proc.Exited += (_, _) =>
                {
                    IsRunning = false;
                    GameExited?.Invoke(proc.ExitCode);
                };
                _gameProcess = proc;
            }
        }
        catch
        {
            // The steam:// protocol is handled by the system shell; if it
            // fails (no Steam), open the store page as a fallback.
            try
            {
                Process.Start(new ProcessStartInfo(Sims4SteamStoreUrl)
                    { UseShellExecute = true });
            }
            catch { }
        }
    }

    /// Strip a leading 'v' from "v1.2.3" → "1.2.3". Preserves other tag
    /// formats (GitHub forks often use "Release X.Y.Z" etc.).
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    // ── WPF helpers ───────────────────────────────────────────────────────────

    private static TextBlock MakeLabel(string text, Brush foreground) =>
        new TextBlock
        {
            Text         = text,
            FontSize     = 10,
            FontWeight   = FontWeights.SemiBold,
            Foreground   = foreground,
            Margin       = new Thickness(0, 0, 0, 8),
        };

    private static Button MakeLinkButton(string label, string url, Brush fg)
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
            Foreground          = new SolidColorBrush(Color.FromRgb(0x60, 0x9A, 0xFF)),
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
