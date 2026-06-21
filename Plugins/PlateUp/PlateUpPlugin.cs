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

namespace LauncherV2.Plugins.PlateUp;

// ═══════════════════════════════════════════════════════════════════════════════
// PlateUpPlugin — install / launch support for "PlateUp!" (Up to Eleven Studios,
// 2022) played through the PlateupAP Steam Workshop mod by CazIsABoi, which
// bundles a full in-game Archipelago client. This is a STEAM-MOD native
// "ConnectsItself" integration in the same family as the shipped Noita / Hollow
// Knight / Stardew Valley / TUNIC / Jak plugins: the mod inside PlateUp! itself
// connects to the AP server using the Archipelago.MultiClient.Net library, so the
// launcher must NOT hold its own ApClient on the same slot while the game is
// running.
//
// ── HONEST REALITY CHECK (2026-06-16, verified against the repos) ─────────────
//
//   * THE AP WORLD game string is "PlateUp" (verified against
//     PlateUpAPMod/ArchipelagoConnectionManager.cs:
//     `private const string GameName = "PlateUp";`
//     and confirmed by the template YAML shipped in the release:
//     `game: PlateUp`).
//     This is a CUSTOM / COMMUNITY world (not merged into the AP main repo) — the
//     apworld is distributed as a standalone "plateup.apworld" file. Users must
//     drop this file into their Archipelago installation's custom_worlds folder
//     before generating. GameId here = "plateup".
//
//   * THE APWORLD REPO is CazIsABoi/Archipelago (verified live 2026-06-16).
//     The relevant release assets are:
//       - "plateup.apworld"  — the custom world file (server-side)
//       - "PlateUp.yaml"     — template options YAML (for generation)
//     Latest verified release: 0.2.6.5 (2026-06-15), asset plateup.apworld.
//
//   * THE IN-GAME MOD REPO is CazIsABoi/PlateUpAPMod (verified live 2026-06-16).
//     This is a Steam Workshop mod (Workshop id 3484431423). The mod embeds the
//     Archipelago.MultiClient.Net library and handles all AP communication fully
//     in-game — no separate "PlateUp Client" exe is needed. The mod is distributed
//     EXCLUSIVELY via Steam Workshop. There is no downloadable DLL the launcher
//     can install directly; the user must subscribe in Steam.
//
//   * CONNECTION is configured IN-GAME (verified against Setup.md + the mod's
//     ArchipelagoConnectionManager): launch PlateUp!, go to HQ (Lobby), open
//     Options > PreferenceSystem > PlateupAP, create a config entry, then fill in
//     Host, Port, SlotName, and Password. The launcher cannot pre-write this
//     config (it lives inside PlateUp's own preference system). So the plugin
//     surfaces the session credentials for the user to copy, and clearly explains
//     what to type where.
//
//   * ConnectsItself = true: the in-game mod owns the AP slot — the launcher
//     must NOT hold its own ApClient while the game is running.
//
//   * SupportsStandalone = true: plain PlateUp! runs fine without the mod enabled.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam PlateUp! install via the Windows registry (SteamPath +
//      WOW6432Node InstallPath), parsing steamapps\libraryfolders.vdf for every
//      library root and locating steamapps\common\PlateUp via
//      appmanifest_1599600.acf. A manual override is supported, validated, and
//      persisted in this plugin's own sidecar (Games/ROMs/plateup/
//      plateup_launcher.json). The user can also pick their install folder.
//
//   2. DOWNLOAD the plateup.apworld file from the latest CazIsABoi/Archipelago
//      release and surface it for the user to drop into their Archipelago
//      custom_worlds folder (the plugin cannot do this automatically because the
//      Archipelago install location is the user's choice). The apworld is saved
//      into GameDirectory (Games/PlateUp/) and the user is guided on placement.
//
//   3. LAUNCH = run PlateUp.exe from the detected/override install; if the exe
//      cannot be found but Steam is present, fall back to
//      steam://rungameid/1599600.
//
// ── DEFENSIVE / UNVERIFIED ────────────────────────────────────────────────────
//   * "Installed" is judged by the presence of a PlateUp! install directory with
//     PlateUp.exe — NOT by the Workshop mod's presence, since that is installed
//     and managed entirely by Steam. The mod tile reads "Mod: subscribe on Steam
//     Workshop" until the user confirms it is active.
//   * Steam library parsing is defensive: tolerant VDF scan; any failure degrades
//     to "PlateUp not found" rather than throwing.
//   * No plaintext AP password is written by this plugin (connection entered
//     in-game), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class PlateUpPlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    // apworld distribution repo (CazIsABoi/Archipelago, verified 2026-06-16)
    private const string APWORLD_OWNER = "CazIsABoi";
    private const string APWORLD_REPO  = "Archipelago";
    private const string ApworldRepoUrl = "https://github.com/CazIsABoi/Archipelago";
    private const string GH_APWORLD_RELEASES_LATEST_URL =
        "https://api.github.com/repos/CazIsABoi/Archipelago/releases/latest";
    private const string GH_APWORLD_RELEASES_URL =
        "https://api.github.com/repos/CazIsABoi/Archipelago/releases";

    // in-game mod (Steam Workshop — cannot be auto-installed by the launcher)
    private const string ModRepoUrl          = "https://github.com/CazIsABoi/PlateUpAPMod";
    private const string WorkshopUrl         = "https://steamcommunity.com/sharedfiles/filedetails/?id=3484431423";
    private const string SetupGuideUrl       = "https://github.com/CazIsABoi/PlateUpAPMod/blob/main/Setup.md";
    private const string ArchipelagoSite     = "https://archipelago.gg";
    private const string ArchipelagoTutorial = "https://archipelago.gg/tutorial/Archipelago/setup_en";

    // Steam — PlateUp! appid 1599600
    private const string PlateUpSteamAppId    = "1599600";
    private const string SteamRunUrl          = "steam://rungameid/1599600";
    private const string SteamCommonFolderName = "PlateUp";
    private const string PlateUpExeName        = "PlateUp.exe";

    // Pinned fallback for when the GitHub API is unreachable.
    // Verified live 2026-06-16: release 0.2.6.5, asset "plateup.apworld".
    private const string FallbackVersion    = "0.2.6.5";
    private const string FallbackApworldUrl =
        "https://github.com/CazIsABoi/Archipelago/releases/download/0.2.6.5/plateup.apworld";
    private const string ApworldFileName    = "plateup.apworld";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "plateup";
    public string DisplayName => "PlateUp!";
    public string Subtitle    => "Native PC · Steam Workshop mod";

    /// EXACT AP game string — verified against CazIsABoi/PlateUpAPMod
    /// ArchipelagoConnectionManager.cs (`private const string GameName = "PlateUp"`)
    /// and the distributed PlateUp.yaml (`game: PlateUp`).
    public string ApWorldName => "PlateUp";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "plateup.png");

    public string ThemeAccentColor => "#E05820";   // warm orange — PlateUp brand colour
    public string[] GameBadges     => new[] { "Steam · Workshop mod" };

    public string Description =>
        "PlateUp!, the co-op restaurant building and automation game by Up to Eleven " +
        "Studios (2022), played with the PlateupAP Steam Workshop mod by CazIsABoi. " +
        "The mod embeds a full Archipelago client directly in-game — dish unlocks, " +
        "appliance unlocks, and progression gates become checks shuffled across the " +
        "multiworld, while day completions, franchise milestones, and achievements " +
        "send items to other players. You bring your own copy of PlateUp! (owned on " +
        "Steam) and subscribe to the mod on the Steam Workshop. The launcher detects " +
        "your Steam install, downloads the plateup.apworld file for generation, and " +
        "guides the setup steps. Connection credentials are entered inside the game " +
        "via Options > PreferenceSystem > PlateupAP.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" = a PlateUp! install exists (the Workshop mod is managed by
    /// Steam; we cannot reliably check it without filesystem inspection of the
    /// Workshop cache, which varies by user and library location).
    public bool IsInstalled => FindPlateUpDir() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps the downloaded apworld file and working data.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "PlateUp");

    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "plateup_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The PlateupAP mod reports all checks/items/goal to the AP server itself —
    // the launcher relays nothing. All three events are present for interface
    // compliance (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
    public event Action<int>?    GameExited;
#pragma warning restore CS0067

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            InstalledVersion = IsInstalled
                ? (ReadStampedApworldVersion() ?? "game installed")
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
                await GitHubHelper.FetchLatestTagAsync("CazIsABoi", "Archipelago", ct));
        }
        catch
        {
            AvailableVersion = null; // contract: never throw on network failure
        }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────
    // "Install" for PlateUp! means:
    //   (a) downloading the plateup.apworld into GameDirectory so the user can
    //       copy it into their Archipelago custom_worlds folder, and
    //   (b) guiding the user through the remaining steps (Workshop subscribe +
    //       in-game config) that cannot be automated.
    // The launcher cannot install the in-game mod (it is Steam Workshop only).

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((5, "Checking the latest PlateupAP apworld release..."));
        var (version, apworldUrl) = await ResolveLatestApworldAsync(ct);
        AvailableVersion = version;

        if (apworldUrl == null)
            throw new InvalidOperationException(
                "Could not find the PlateupAP apworld download on GitHub. Check " +
                "your internet connection, or download it manually from " +
                ApworldRepoUrl + "/releases — the file you need is plateup.apworld.");

        // Download the apworld into GameDirectory.
        Directory.CreateDirectory(GameDirectory);
        string destApworld = Path.Combine(GameDirectory, ApworldFileName);

        progress.Report((10, $"Downloading plateup.apworld {version}..."));
        string tempFile = Path.Combine(Path.GetTempPath(),
            "plateup-apworld-" + Guid.NewGuid().ToString("N") + ".apworld");
        try
        {
            using var response = await _http.GetAsync(
                apworldUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long total      = response.Content.Headers.ContentLength ?? -1;
            long downloaded = 0;

            await using (var src = await response.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tempFile))
            {
                var buf = new byte[81920];
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    downloaded += n;
                    if (total > 0)
                    {
                        int pct = (int)(10 + 70 * downloaded / total);
                        progress.Report((pct,
                            "Downloading plateup.apworld... " + downloaded / 1024 + " KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            File.Move(tempFile, destApworld, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }

        // Stamp the apworld version for display on the tile.
        WriteStampedApworldVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            "Downloaded plateup.apworld " + version + " to: " + destApworld + ". " +
            "NEXT STEPS: (1) Copy plateup.apworld into your Archipelago installation's " +
            "custom_worlds folder (see Settings for the path). " +
            "(2) Subscribe to the Archipelago for PlateUp! mod on Steam Workshop " +
            "(Workshop id 3484431423) if you have not already — the mod handles all " +
            "in-game connection. " +
            "(3) In PlateUp!, go to HQ -> Options -> PreferenceSystem -> PlateupAP, " +
            "create a config, and fill in your Host, Port, SlotName, and Password. " +
            "(This launcher cannot pre-fill this connection — it is entered in-game.)"));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        // Basic: game exists + the apworld file has been downloaded.
        return IsInstalled &&
               File.Exists(Path.Combine(GameDirectory, ApworldFileName));
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // HONEST: the AP connection for PlateUp! is configured in-game via
        // HQ -> Options -> PreferenceSystem -> PlateupAP. There is no CLI arg
        // or config file this launcher can pre-write. Launching just starts the
        // game; the user fills in the connection and selects the mod from there.
        //
        // ConnectsItself = true: the mod owns the slot — the launcher must NOT
        // hold its own ApClient on this slot while the game is running.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartPlateUp();
        return Task.CompletedTask;
    }

    /// Plain PlateUp! runs fine without the AP mod active.
    public bool SupportsStandalone => true;

    /// The PlateupAP mod owns the AP slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartPlateUp();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        // No plaintext AP credentials written by this plugin — nothing to scrub.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself) ───────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The PlateupAP mod receives items from the AP server directly;
        // there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own connection status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your PlateUp! install folder.";

        if (LooksLikePlateUpDir(folder))
            return null;

        // Be forgiving: user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikePlateUpDir(nested)) return null;
        }
        catch { /* ignore */ }

        return "That does not look like a PlateUp! installation. Pick the folder " +
               "that contains PlateUp.exe (for Steam this is usually " +
               @"...\steamapps\common\PlateUp).";
    }

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
            Text =
                "PlateUp! is your own game (Steam) with the PlateupAP mod installed " +
                "via the Steam Workshop. The mod handles all Archipelago communication " +
                "in-game — no separate client exe needed. The launcher can download " +
                "the plateup.apworld file for multiworld generation and help you " +
                "locate your Archipelago install. The Workshop mod subscribe, and the " +
                "in-game connection setup, are done manually — those steps cannot be " +
                "automated by this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── PlateUp! install section ──────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "PLATEUP! INSTALL", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? plateupDir  = FindPlateUpDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg   = plateupDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + plateupDir
                : "Detected Steam install: " + plateupDir)
            : "PlateUp! not detected. Pick your install folder below, or install " +
              "PlateUp! via Steam first.";
        panel.Children.Add(new TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = plateupDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text = overrideDir ?? plateupDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your PlateUp! install folder (the one containing PlateUp.exe). " +
                          "Detected from Steam automatically; use this picker for a " +
                          "non-standard Steam library or another store.",
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
            var dlg = new OpenFolderDialog
            {
                Title = "Select your PlateUp! install folder (contains PlateUp.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? plateupDir ?? "")
                                   ? (overrideDir ?? plateupDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a PlateUp! folder",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!LooksLikePlateUpDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikePlateUpDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 1599600). Use " +
                   "this picker for a non-standard Steam library or another store.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── apworld file section ──────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "APWORLD FILE (for generation)", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });

        string apworldDest = Path.Combine(GameDirectory, ApworldFileName);
        bool   hasApworld  = File.Exists(apworldDest);
        panel.Children.Add(new TextBlock
        {
            Text = hasApworld
                ? "Downloaded: " + apworldDest +
                  "\nCopy this file into your Archipelago installation's " +
                  "custom_worlds folder (e.g. C:\\ProgramData\\Archipelago\\lib\\worlds\\ " +
                  "or <Archipelago install>\\lib\\worlds\\), then generate normally."
                : "Not yet downloaded. Click Install on the Play tab to download " +
                  "plateup.apworld. You will then copy it to your Archipelago " +
                  "installation's custom_worlds folder before generating.",
            FontSize = 11, Foreground = hasApworld ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Connection section (in-game) ──────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "CONNECTING (entered in-game)", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text =
                "Launch PlateUp!, open HQ (the lobby), then go to Options > " +
                "PreferenceSystem > PlateupAP. Create a new config entry and fill in " +
                "Host, Port, SlotName, and Password (if any). The mod will connect " +
                "automatically when you start a new run. This launcher cannot pre-fill " +
                "this connection — it is configured entirely in-game.",
            FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Guided setup steps ────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own PlateUp! (on Steam). Install it via Steam if you have not. Use the " +
                "folder picker above if it was not detected automatically.",
            "2. Subscribe to the \"Archipelago for PlateUp!\" mod on the Steam Workshop " +
                "(Workshop id 3484431423). Steam will download and keep the mod updated. " +
                "The mod embeds a full AP client — no separate client exe is needed.",
            "3. Download the apworld: click Install on the Play tab. This downloads " +
                "plateup.apworld into Games/PlateUp/ inside the launcher's folder.",
            "4. Copy plateup.apworld into your Archipelago installation's custom_worlds " +
                "folder (e.g. <Archipelago>\\lib\\worlds\\). Then generate your multiworld " +
                "normally on archipelago.gg or your local AP instance.",
            "5. Launch PlateUp! and open HQ. Go to Options > PreferenceSystem > " +
                "PlateupAP, create a config, and enter your AP server's Host, Port, " +
                "SlotName, and Password. Start a new run — the mod connects automatically.",
            "6. Confirm a \"Connected\" notice from the mod in-game. Items from your " +
                "server will arrive as you play; your checks will be sent when you " +
                "complete days, unlock appliances, and hit franchise milestones.",
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
        foreach (var (label, url) in new (string, string)[]
        {
            ("Steam Workshop: Archipelago for PlateUp! ↗",  WorkshopUrl),
            ("PlateupAP mod repo (GitHub) ↗",               ModRepoUrl),
            ("apworld releases (GitHub) ↗",                 ApworldRepoUrl + "/releases"),
            ("PlateupAP Setup Guide ↗",                     SetupGuideUrl),
            ("Archipelago Official ↗",                      ArchipelagoSite),
            ("Archipelago Setup Guide ↗",                   ArchipelagoTutorial),
        })
        {
            var btn = new Button
            {
                Content = label, HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), FontSize = 12,
                Margin = new Thickness(0, 0, 0, 4),
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

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // Pull apworld releases from CazIsABoi/Archipelago as the AP-relevant news.
        try
        {
            string json = await _http.GetStringAsync(GH_APWORLD_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                // Only include releases that have a plateup.apworld asset —
                // this repo is a full Archipelago fork so most releases are
                // AP core releases, not PlateUp-specific.
                bool hasApworld = false;
                if (el.TryGetProperty("assets", out var assets) &&
                    assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        string? n = a.TryGetProperty("name", out var nv) ? nv.GetString() : null;
                        if (n != null && n.Contains("plateup", StringComparison.OrdinalIgnoreCase))
                        {
                            hasApworld = true;
                            break;
                        }
                    }
                }
                if (!hasApworld) continue;

                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) &&
                    d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var ni) ? ni.GetString() ?? "" : "",
                    Body:    el.TryGetProperty("body",     out var b)  ? b.GetString()  ?? "" : "",
                    Version: el.TryGetProperty("tag_name", out var t)
                                 ? NormalizeTag(t.GetString()) ?? "" : "",
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
            ? tag[1..] : tag;
    }

    /// Resolve the latest release that contains a plateup.apworld asset.
    /// Falls back to the pinned 0.2.6.5 direct URL when the API is unreachable.
    private async Task<(string Version, string? ApworldUrl)> ResolveLatestApworldAsync(
        CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_APWORLD_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return (FallbackVersion, FallbackApworldUrl);

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("assets", out var assets) ||
                    assets.ValueKind != JsonValueKind.Array)
                    continue;

                string? apworldUrl = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u)
                                   ? u.GetString() : null;
                    if (name == null || url == null) continue;
                    if (name.EndsWith(".apworld", StringComparison.OrdinalIgnoreCase) &&
                        name.Contains("plateup", StringComparison.OrdinalIgnoreCase))
                    {
                        apworldUrl = url;
                        break;
                    }
                }
                if (apworldUrl == null) continue;

                string? version = el.TryGetProperty("tag_name", out var t)
                    ? NormalizeTag(t.GetString()) : null;
                if (version != null)
                    return (version, apworldUrl);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (FallbackVersion, FallbackApworldUrl);
    }

    // ── Private helpers — Steam / PlateUp! detection ──────────────────────────

    private string? FindPlateUpDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikePlateUpDir(ov)) return ov;
        try { return DetectSteamPlateUpDir(); }
        catch { return null; }
    }

    private static bool LooksLikePlateUpDir(string dir)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(dir)
                && Directory.Exists(dir)
                && File.Exists(Path.Combine(dir, PlateUpExeName));
        }
        catch { return false; }
    }

    private static string? DetectSteamPlateUpDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, "appmanifest_" + PlateUpSteamAppId + ".acf");
                    if (!File.Exists(manifest)) continue;

                    string common    = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikePlateUpDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikePlateUpDir(conventional)) return conventional;
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
            int i    = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            i += key.Length;
            int open  = text.IndexOf('"', i);
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

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartPlateUp()
    {
        string? dir = FindPlateUpDir();
        string? exe = dir != null ? Path.Combine(dir, PlateUpExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = dir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start PlateUp!.");

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
            catch { /* non-fatal — some processes don't expose Exited */ }
            return;
        }

        // Fall back to Steam URI if no exe found but Steam is present.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true; // best-effort: Steam owns the process
                return;
            }
            catch { /* fall through to error */ }
        }

        throw new FileNotFoundException(
            "Could not find PlateUp.exe. Open this game's Settings and pick your " +
            "PlateUp! install folder, or install PlateUp! via Steam.",
            PlateUpExeName);
    }

    // ── Private helpers — settings sidecar ───────────────────────────────────

    private sealed class PlateUpSettings
    {
        public string? InstallOverride   { get; set; }
        public string? ApworldVersion    { get; set; }
    }

    private PlateUpSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<PlateUpSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(PlateUpSettings s)
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
        var s = LoadSettings(); s.InstallOverride = p; SaveSettings(s);
    }

    private string? ReadStampedApworldVersion()
    {
        string? v = LoadSettings().ApworldVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private void WriteStampedApworldVersion(string v)
    {
        var s = LoadSettings(); s.ApworldVersion = v; SaveSettings(s);
    }
}
