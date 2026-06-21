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

namespace LauncherV2.Plugins.OriBlindForest;

// ═══════════════════════════════════════════════════════════════════════════════
// OriBlindForestPlugin — Ori and the Blind Forest: Definitive Edition
// Archipelago Randomizer mod by c-ostic (OriBFArchipelago).
//
// ── HONEST REALITY CHECK (2026-06-15, verified against the mod repo and apworld) ──
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of Ori and the Blind Forest: Definitive Edition (Steam appid 387290), and
// Archipelago support is delivered as a BepInEx mod added on top. The verified
// facts:
//
//   * THE AP WORLD game string is "Ori and the Blind Forest" (verified against
//     oribf/__init__.py: `class OriBlindForestWorld(World): ... game = "Ori and
//     the Blind Forest"`). This is a COMMUNITY world — the apworld is distributed
//     separately from the main Archipelago release via c-ostic/Archipelago fork.
//     GameId = "oribf".
//
//   * TWO REPOS:
//       Client mod: c-ostic/OriBFArchipelago (latest: v0.4.0, 2026-03-07)
//                   Asset: OriBFArchipelago.zip → BepInEx\plugins\OriBFArchipelago\
//       APWorld:    c-ostic/Archipelago fork (latest: v0.4.2, 2026-03-10)
//                   Asset: oribf.apworld (for the AP host — NOT installed here)
//
//   * BEPINEX PREREQUISITE: Ori and the Blind Forest: DE is a Unity Mono (x86)
//     game. The mod requires BepInEx x86 5.4.23.2 (NOT IL2CPP). The mod README
//     specifically states: "download the x86 version specifically even if your
//     machine is x64." BepInEx 5 x86 is a portable zip extracted into the game
//     root. The mod zip does NOT bundle BepInEx.
//
//   * ADDITIONAL BEPINEX CONFIG STEP (verified from Setup.md): after installing
//     BepInEx and running the game once, the user must edit BepInEx.cfg to change
//     the logging Type line from "Type = Application" to "Type = Camera" —
//     otherwise the game fails to load the mod. This plugin surfaces this as a
//     guided step; it cannot automate it safely (the file only exists after the
//     first BepInEx-instrumented launch, and the exact line must be present).
//
//   * CONNECTION is made IN-GAME (verified from Setup.md): the mod adds text boxes
//     to the SAVE SLOT SELECTION SCREEN where the player types the server name,
//     port, slot name, and optional password. The mod contains its own AP client
//     bridge — ConnectsItself = true. The launcher must NOT hold its own ApClient
//     on the same slot while the game is running.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam install via registry (SteamPath / InstallPath) →
//      libraryfolders.vdf → appmanifest_387290.acf. A manual folder override
//      is also supported and takes precedence; validated (must contain
//      OriDE.exe) and persisted in a plugin sidecar file.
//   2. INSTALL/UPDATE (best effort):
//      (a) If BepInEx 5 x86 is not present, download the pinned
//          BepInEx_x86_5.4.23.2.zip and extract it into the game root.
//      (b) Download the OriBFArchipelago.zip from the latest client-mod release
//          and extract the "OriBFArchipelago" folder into BepInEx\plugins\.
//      Then surface the BepInEx.cfg edit and first-run steps clearly.
//   3. LAUNCH = run OriDE.exe from the detected/override install; fall back to
//      steam://rungameid/387290 when the exe is missing but Steam is present.
//      ConnectsItself = true. SupportsStandalone = true.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class OriBlindForestPlugin : IGamePlugin
{
    // ── Client-mod repo (c-ostic/OriBFArchipelago) ────────────────────────────
    private const string MOD_OWNER = "c-ostic";
    private const string MOD_REPO  = "OriBFArchipelago";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    // ── APWorld repo (c-ostic/Archipelago fork) — informational only ──────────
    private const string ApworldRepoUrl = "https://github.com/c-ostic/Archipelago";

    // ── BepInEx 5 x86 (Mono, NOT IL2CPP) — the required mod loader ───────────
    // Ori: DE is Unity Mono x86. The Setup.md explicitly requires x86 even on x64.
    private const string BepInExSite = "https://github.com/BepInEx/BepInEx/releases";
    private const string BepInExVersion = "5.4.23.2";
    private static readonly string BepInExZipUrl =
        "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/" +
        "BepInEx_x86_5.4.23.2.zip";

    // ── Pinned mod fallback (v0.4.0, verified 2026-06-15) ────────────────────
    private const string FallbackModVersion = "0.4.0";
    private const string FallbackModTag     = "v0.4.0";
    private const string FallbackModZipName = "OriBFArchipelago.zip";
    private static readonly string FallbackModZipUrl =
        $"{ModRepoUrl}/releases/download/{FallbackModTag}/{FallbackModZipName}";

    // ── Steam / game constants ────────────────────────────────────────────────
    private const string OriSteamAppId       = "387290";
    private static readonly string SteamRunUrl = $"steam://rungameid/{OriSteamAppId}";
    private const string OriExeName           = "OriDE.exe";
    private const string OriDataFolder        = "OriDE_Data";
    private const string SteamCommonFolderName = "Ori and the Blind Forest DE";

    // ── Mod folder name inside BepInEx\plugins ────────────────────────────────
    private const string ModFolderName = "OriBFArchipelago";
    private const string ModPrimaryDll = "OriBFArchipelago.dll";

    // ── Links ─────────────────────────────────────────────────────────────────
    private const string SetupGuideUrl   = $"{ModRepoUrl}/blob/main/Setup.md";
    private const string ArchipelagoSite = "https://archipelago.gg";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "oribf";
    public string DisplayName => "Ori and the Blind Forest";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against oribf/__init__.py:
    ///   class OriBlindForestWorld(World): ... game = "Ori and the Blind Forest"
    public string ApWorldName => "Ori and the Blind Forest";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "oribf.png");

    public string ThemeAccentColor => "#7ABECC";   // spirit-blue glow
    public string[] GameBadges     => new[] { "Steam · needs mod" };

    public string Description =>
        "Ori and the Blind Forest: Definitive Edition (Moon Studios, 2016) " +
        "played with the OriBFArchipelago randomizer mod by c-ostic, which " +
        "bundles an in-game Archipelago client. Skills, life-cells, energy-cells, " +
        "keystones, mapstones and more are shuffled across the multiworld. " +
        "You bring your own copy of Ori: DE (owned on Steam); the mod runs on " +
        "BepInEx 5 x86. The launcher detects your Steam install, stages BepInEx " +
        "and the mod, and guides the required one-time BepInEx config edit. " +
        "You connect to your server by typing credentials at the in-game save " +
        "slot selection screen — no external client needed.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    public bool IsInstalled => FindInstalledModDll() != null;
    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "OriBlindForest");

    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "oribf_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The OriBFArchipelago mod reports checks/items/goal to the AP server itself.
    // These events exist for interface compatibility (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── ConnectsItself / SupportsStandalone ───────────────────────────────────

    /// The mod owns the AP slot connection. The launcher must NOT hold its own
    /// ApClient on this slot while the mod is running.
    public bool ConnectsItself => true;

    /// Ori: DE runs fine standalone (no AP connection required).
    public bool SupportsStandalone => true;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            InstalledVersion = FindInstalledModDll() != null
                ? (ReadStampedVersion() ?? "installed")
                : null;
        }
        catch { InstalledVersion = null; }
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
        progress.Report((2, "Locating your Ori: Definitive Edition installation..."));
        string? oriDir = ResolveOriDir();
        if (oriDir == null)
            throw new InvalidOperationException(
                "Could not find Ori and the Blind Forest: DE. Open this game's Settings " +
                "and pick your Ori: DE folder (the one containing OriDE.exe), or install " +
                "it via Steam first.");

        progress.Report((6, "Checking the latest OriBFArchipelago release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the OriBFArchipelago download on GitHub. Check your " +
                "internet connection, or download it manually from " + ModRepoUrl + "/releases.");

        // BepInEx 5 x86 — required mod loader (Mono game, NOT IL2CPP).
        // The mod README explicitly requires the x86 build even on 64-bit machines.
        if (!BepInExPresent(oriDir))
        {
            progress.Report((12, "Staging BepInEx 5 x86 into your Ori: DE folder..."));
            await DownloadAndExtractZipToDirAsync(
                BepInExZipUrl, $"ori-bepinex-{BepInExVersion}", oriDir, 12, 45, progress, ct);
        }
        else
        {
            progress.Report((45, "BepInEx already present — keeping your existing install."));
        }

        // Extract the OriBFArchipelago folder into BepInEx\plugins.
        string pluginsDir = Path.Combine(oriDir, "BepInEx", "plugins");
        await DownloadAndExtractModAsync(zipUrl, version, pluginsDir, 48, 92, progress, ct);

        WriteStampedVersion(version);
        InstalledVersion = version;

        bool bepOk  = BepInExPresent(oriDir);
        string cfg  = Path.Combine(oriDir, "BepInEx", "config", "BepInEx.cfg");
        bool cfgOk  = TryPatchBepInExConfig(cfg);

        progress.Report((100,
            $"OriBFArchipelago {version} installed." +
            (bepOk ? " BepInEx is present." : "") +
            (cfgOk
                ? " BepInEx.cfg patched automatically (Type = Camera)."
                : " NOTE: BepInEx.cfg not found yet — you need to run the game once " +
                  "so BepInEx generates it, then open Settings for the config step.") +
            " Next: launch Ori: DE once to let BepInEx finish setting up, confirm the " +
            "mod is loaded, then select a save slot to enter your AP connection details."));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    /// ConnectsItself = true: no credential prefill — the player enters connection
    /// details at the in-game save slot selection screen.
    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        _ = session; // entered in-game; no documented prefill mechanism
        StartOri();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartOri();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Existing-install validation ───────────────────────────────────────────

    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Ori: DE install folder.";
        if (LooksLikeOriDir(folder)) return null;
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeOriDir(nested)) return null;
        }
        catch { }
        return "That does not look like an Ori: Definitive Edition installation. " +
               "Pick the folder that contains OriDE.exe (for Steam this is usually " +
               @"...\steamapps\common\Ori and the Blind Forest DE).";
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
            Text = "Ori and the Blind Forest: DE is your own game (Steam) with the " +
                   "OriBFArchipelago mod added via BepInEx 5 x86. The launcher stages " +
                   "BepInEx and the mod for you. You must run the game once so BepInEx " +
                   "generates its config, then check the BepInEx.cfg step below. " +
                   "Connection is entered at the in-game save slot selection screen.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install ─────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "ORI: DE INSTALL", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? oriDir      = ResolveOriDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg   = oriDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + oriDir
                : "Detected Steam install: " + oriDir)
            : "Ori: DE not detected. Pick your install folder below, or install it " +
              "via Steam first.";
        panel.Children.Add(new TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = oriDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        bool bepOk = oriDir != null && BepInExPresent(oriDir);
        panel.Children.Add(new TextBlock
        {
            Text = oriDir == null ? "" :
                   bepOk ? "BepInEx found in your Ori: DE folder." :
                           "BepInEx not found yet — Install on the Play tab stages it.",
            FontSize = 11, Foreground = bepOk ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });

        string? modDll = FindInstalledModDll();
        panel.Children.Add(new TextBlock
        {
            Text = modDll != null
                ? "OriBFArchipelago mod found: " + modDll
                : "OriBFArchipelago mod not found yet (use Install on the Play tab).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        // BepInEx.cfg status
        string? cfg    = oriDir != null ? Path.Combine(oriDir, "BepInEx", "config", "BepInEx.cfg") : null;
        bool    cfgExists = cfg != null && File.Exists(cfg);
        bool    cfgOk     = cfgExists && IsBepInExConfigPatched(cfg!);
        if (oriDir != null)
        {
            string cfgText = !cfgExists
                ? "BepInEx.cfg not found yet. Run the game once after BepInEx is installed."
                : cfgOk
                    ? "BepInEx.cfg is correctly set (Type = Camera)."
                    : "BepInEx.cfg needs editing: change \"Type = Application\" to \"Type = Camera\". " +
                      "Use the Fix Config button below.";
            panel.Children.Add(new TextBlock
            {
                Text = cfgText, FontSize = 11, Foreground = cfgOk ? success : warn,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
            });

            if (cfgExists && !cfgOk)
            {
                var fixBtn = new System.Windows.Controls.Button
                {
                    Content = "Fix BepInEx.cfg automatically", Width = 220,
                    Padding = new Thickness(0, 6, 0, 6), Margin = new Thickness(0, 0, 0, 8),
                    Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
                    Foreground  = fg,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                fixBtn.Click += (_, _) =>
                {
                    bool patched = TryPatchBepInExConfig(cfg!);
                    MessageBox.Show(patched
                        ? "BepInEx.cfg patched: Type is now set to Camera."
                        : "Could not patch BepInEx.cfg automatically. Edit it manually: " +
                          "open " + cfg + " and change \"Type = Application\" to \"Type = Camera\".",
                        "BepInEx Config",
                        MessageBoxButton.OK,
                        patched ? MessageBoxImage.Information : MessageBoxImage.Warning);
                };
                panel.Children.Add(fixBtn);
            }
        }

        // Folder picker
        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text = overrideDir ?? oriDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip = "Your Ori: DE install folder (the one containing OriDE.exe). " +
                      "Detected from Steam automatically; set it here to override.",
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
                Title            = "Select your Ori: Definitive Edition folder (contains OriDE.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? oriDir ?? "")
                                   ? (overrideDir ?? oriDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    MessageBox.Show(bad, "Not an Ori: DE folder",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!LooksLikeOriDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeOriDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 387290). Use this " +
                   "picker for a non-standard Steam library or another store.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection ───────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "CONNECTING (entered in-game)", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "On the save slot selection screen, the mod adds text boxes for " +
                   "Server Name, Port, Slot Name, and Password. Fill them in and press " +
                   "Enter/the connect button to join your multiworld session. This " +
                   "launcher does not pre-fill the connection.",
            FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Ori and the Blind Forest: Definitive Edition (Steam). Install it if " +
               "you have not. Use the folder picker above if it was not auto-detected.",
            "2. Click Install on the Play tab. This downloads BepInEx 5 x86 and the " +
               "OriBFArchipelago mod and drops them into your Ori: DE folder.",
            "3. Launch Ori: DE ONCE so BepInEx generates its config folder. You do not " +
               "need to play — just reach the title screen and quit.",
            "4. Check the BepInEx.cfg step above. The launcher will patch it automatically " +
               "after step 3 (use the Fix Config button if needed). Without this the mod " +
               "will not load.",
            "5. Launch Ori: DE again. On the save slot selection screen, enter your AP " +
               "Server, Port, Slot Name, and Password to connect to your multiworld.",
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
            ("OriBFArchipelago mod (GitHub) ↗",  ModRepoUrl),
            ("OriBFArchipelago Setup Guide ↗",   SetupGuideUrl),
            ("APWorld repo (c-ostic/Archipelago) ↗", ApworldRepoUrl),
            ("BepInEx (releases) ↗",             BepInExSite),
            ("Archipelago Official ↗",           ArchipelagoSite),
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
                try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); } catch { }
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
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<NewsItem>();

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

    /// Resolve the latest mod release: version + zip URL.
    /// Falls back to the pinned v0.4.0 download when GitHub is unreachable.
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
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        return (version, url);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable → pinned fallback */ }

        return (FallbackModVersion, FallbackModZipUrl);
    }

    // ── Private helpers — Steam / Ori detection ───────────────────────────────

    private string? ResolveOriDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeOriDir(ov)) return ov;
        try { return DetectSteamOriDir(); }
        catch { return null; }
    }

    private static bool LooksLikeOriDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, OriExeName))) return true;
            if (Directory.Exists(Path.Combine(dir, OriDataFolder))) return true;
            return false;
        }
        catch { return false; }
    }

    /// True when BepInEx 5 appears installed: the BepInEx folder is present, or
    /// winhttp.dll is at the game root (the BepInEx 5 proxy DLL).
    private static bool BepInExPresent(string oriDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(oriDir) || !Directory.Exists(oriDir)) return false;
            if (Directory.Exists(Path.Combine(oriDir, "BepInEx"))) return true;
            if (File.Exists(Path.Combine(oriDir, "winhttp.dll"))) return true;
            return false;
        }
        catch { return false; }
    }

    private static string? DetectSteamOriDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{OriSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common    = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeOriDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeOriDir(conventional)) return conventional;
                }
                catch { }
            }
        }
        return null;
    }

    private static IEnumerable<string> SteamRoots()
    {
        string? hkcu = ReadRegistryString(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
        if (!string.IsNullOrWhiteSpace(hkcu)) yield return NormalizeSteamPath(hkcu);

        string? hklm = ReadRegistryString(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm)) yield return NormalizeSteamPath(hklm);

        string? hklm64 = ReadRegistryString(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm64)) yield return NormalizeSteamPath(hklm64);

        string? progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86)) yield return Path.Combine(progX86, "Steam");
    }

    private static string NormalizeSteamPath(string p) => p.Replace('/', '\\').TrimEnd('\\');

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
            int open  = text.IndexOf('"', i);
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

    // ── Private helpers — installed-mod detection ─────────────────────────────

    private string? FindInstalledModDll()
    {
        try
        {
            string? ori = ResolveOriDir();
            if (ori == null) return null;
            string bepInExDir = Path.Combine(ori, "BepInEx");
            if (!Directory.Exists(bepInExDir)) return null;
            foreach (string dll in Directory.EnumerateFiles(
                bepInExDir, "*.dll", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(dll).Equals(ModPrimaryDll, StringComparison.OrdinalIgnoreCase))
                    return dll;
            }
        }
        catch { }
        return null;
    }

    // ── Private helpers — BepInEx.cfg patch ──────────────────────────────────

    /// OriBFArchipelago Setup.md: after the first BepInEx run, edit BepInEx.cfg to
    /// change "Type = Application" to "Type = Camera" in the [Logging.Console]
    /// section — otherwise the mod fails to load (Unity window-type conflict).
    /// Returns true when the file was already correct or was patched successfully.
    private static bool TryPatchBepInExConfig(string cfgPath)
    {
        try
        {
            if (!File.Exists(cfgPath)) return false;
            string text = File.ReadAllText(cfgPath);
            if (IsBepInExConfigPatched(text)) return true; // already correct

            // Replace the first occurrence of "Type = Application" with "Type = Camera".
            // Be conservative: only change the exact phrase (case-insensitive) to avoid
            // touching unrelated Type lines.
            string patched = System.Text.RegularExpressions.Regex.Replace(
                text,
                @"(?i)\bType\s*=\s*Application\b",
                "Type = Camera",
                System.Text.RegularExpressions.RegexOptions.None);

            if (patched == text) return false; // nothing changed (line not present)
            File.WriteAllText(cfgPath, patched, new UTF8Encoding(false));
            return true;
        }
        catch { return false; }
    }

    private static bool IsBepInExConfigPatched(string cfgPath)
    {
        try
        {
            string text = File.Exists(cfgPath) ? File.ReadAllText(cfgPath) : cfgPath;
            return text.IndexOf("Type = Camera", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return false; }
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartOri()
    {
        string? ori = ResolveOriDir();
        string? exe = ori != null ? Path.Combine(ori, OriExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = ori!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Ori: DE.");

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
            return;
        }

        // Fall back to Steam if available.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
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
            "Could not find OriDE.exe. Open this game's Settings and pick your " +
            "Ori: DE install folder, or install it via Steam.",
            OriExeName);
    }

    // ── Private helpers — download / extract ──────────────────────────────────

    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string pluginsDir,
        int pctStart,
        int pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip     = Path.Combine(Path.GetTempPath(),
            $"oribf-mod-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"oribf-mod-x-{version}-{Guid.NewGuid():N}");
        try
        {
            int dlEnd = pctStart + (pctEnd - pctStart) * 7 / 10;
            await DownloadFileAsync(zipUrl, tempZip,
                $"Downloading OriBFArchipelago {version}...", pctStart, dlEnd, progress, ct);

            progress.Report((dlEnd, "Installing mod into BepInEx\\plugins..."));
            Directory.CreateDirectory(pluginsDir);
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            string? srcModDir = FindModSourceDir(tempExtract);
            string destModDir = Path.Combine(pluginsDir, ModFolderName);
            if (Directory.Exists(destModDir))
            {
                try { Directory.Delete(destModDir, recursive: true); } catch { }
            }

            if (srcModDir != null)
                CopyDirectory(srcModDir, destModDir);
            else
                CopyDirectory(tempExtract, destModDir);

            progress.Report((pctEnd, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip))        File.Delete(tempZip); }        catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, true); } catch { }
        }
    }

    private static string? FindModSourceDir(string root)
    {
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(dir).Equals(ModFolderName, StringComparison.OrdinalIgnoreCase))
                    return dir;
            }
            foreach (string dll in Directory.EnumerateFiles(root, ModPrimaryDll, SearchOption.AllDirectories))
                return Path.GetDirectoryName(dll);
        }
        catch { }
        return null;
    }

    private async Task DownloadAndExtractZipToDirAsync(
        string zipUrl,
        string tag,
        string targetDir,
        int pctStart,
        int pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(), $"{tag}-{Guid.NewGuid():N}.zip");
        try
        {
            int dlEnd = pctStart + (pctEnd - pctStart) * 8 / 10;
            await DownloadFileAsync(zipUrl, tempZip,
                "Downloading BepInEx 5 x86...", pctStart, dlEnd, progress, ct);

            progress.Report((dlEnd, "Extracting BepInEx into your Ori: DE folder..."));
            Directory.CreateDirectory(targetDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, targetDir, overwriteFiles: true);
            progress.Report((pctEnd, "BepInEx staged."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    private async Task DownloadFileAsync(
        string url,
        string destPath,
        string msg,
        int pctStart,
        int pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        progress.Report((pctStart, msg));
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long total      = response.Content.Headers.ContentLength ?? -1;
        long downloaded = 0;

        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destPath);
        var buf = new byte[81920];
        int bytesRead;
        while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
            downloaded += bytesRead;
            if (total > 0)
            {
                int span = Math.Max(1, pctEnd - pctStart);
                int pct  = pctStart + (int)(span * downloaded / total);
                progress.Report((pct, $"{msg} {downloaded / 1000}KB"));
            }
        }
        await dst.FlushAsync(ct);
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceDir, file);
            string dst = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(file, dst, overwrite: true);
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class OriBFSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private OriBFSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<OriBFSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(OriBFSettings s)
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

    private string? LoadOverrideDir()
    {
        string? p = LoadSettings().InstallOverride;
        return string.IsNullOrWhiteSpace(p) ? null : p;
    }

    private void SaveOverrideDir(string p)
    {
        var s = LoadSettings(); s.InstallOverride = p; SaveSettings(s);
    }

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().ModVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private void WriteStampedVersion(string v)
    {
        var s = LoadSettings(); s.ModVersion = v; SaveSettings(s);
    }
}
