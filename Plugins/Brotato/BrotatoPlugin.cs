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
using Microsoft.Win32;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Brotato;

// ═══════════════════════════════════════════════════════════════════════════════
// BrotatoPlugin — install / launch for "Brotato" (Blobfish, 2023),
// played through the Archipelago mod "RampagingHippy-Archipelago" (GitHub:
// SpenserHaddad/Brotato-ArchipelagoClient). This is a NATIVE "ConnectsItself"
// integration — Brotato is a Godot roguelike on Steam that uses an in-game
// ModLoader mod to connect to the Archipelago server. No emulator, no Lua bridge.
//
// ── VERIFIED FACTS (2026-06-14, researched from GitHub repo + README) ─────────
//
//   * THE AP WORLD game string is "Brotato" (verified against
//     apworld/brotato/archipelago.json: `"game": "Brotato"`). GameId = "brotato".
//     Latest release: v0.15.0 (2026-05-12), compatible with Brotato 1.1.15.0.
//
//   * THE MOD is "RampagingHippy-Archipelago" (GitHub release zip:
//     RampagingHippy-Archipelago.zip). Brotato uses ModLoader (a Godot mod system)
//     NOT BepInEx. Mod namespace: "RampagingHippy", name: "Archipelago".
//
//   * STEAM INSTALL (THE CRITICAL CONSTRAINT): As of Brotato v1.1.0.0, Brotato
//     only loads Workshop-registered mods. The official workaround is:
//       1. Subscribe to the "[Modders] placeholder" Workshop item (ID 3369699033).
//          This downloads a placeholder mod folder:
//          ...\steamapps\workshop\content\1942280\3369699033\
//       2. Copy RampagingHippy-Archipelago.zip (unextracted) INTO that folder.
//          The ModLoader picks it up from there — DO NOT unzip.
//     Alternatively, users can subscribe to the official Steam Workshop AP mod
//     (search "Archipelago" in Brotato Workshop) for automatic updates.
//
//   * MOD DETECTION: The mod is installed when the zip file
//     "RampagingHippy-Archipelago.zip" exists inside the placeholder mod folder
//     ..\steamapps\workshop\content\1942280\3369699033\.
//
//   * CONNECTION CONFIG: The mod reads AP connection settings from Godot's user
//     config directory (set by ModLoaderConfig in _ready()). The config lives at:
//       %APPDATA%\Godot\app_userdata\Brotato\mods-config\
//           RampagingHippy-Archipelago\ap_config.json
//     The JSON keys (from manifest.json config_schema): "ap_server", "ap_player",
//     "ap_password", "has_saved_run" (bool), "deathlink_mode" (int 0–2).
//     Writing this file PRE-FILLS the in-game connection form with server/slot/pwd.
//     The config's older keys (from config.json): "last_server", "last_player",
//     "last_password" — also writable for fallback compat.
//
//   * LAUNCH: Launch via Steam (steam://rungameid/1942280). No CLI args exist for
//     Brotato to specify AP connection; the mod reads its own config file.
//
//   * ConnectsItself = true (the mod owns the slot on the AP server).
//   * SupportsStandalone = true (Brotato works normally without the mod connected).
//
//   * Xbox / Game Pass version CANNOT be used (no ModLoader support).
//
// ── WHAT THIS PLUGIN DOES ─────────────────────────────────────────────────────
//   1. DETECT the Steam Brotato install via registry + libraryfolders.vdf
//      (appid 1942280), with a manual override path stored in the sidecar.
//   2. CHECK if the placeholder Workshop mod folder (ID 3369699033) exists, which
//      is required for the mod zip to be loaded. Surface a clear guide when missing.
//   3. INSTALL/UPDATE: download RampagingHippy-Archipelago.zip from GitHub releases
//      and drop it (un-extracted) into the placeholder Workshop folder. Also writes
//      the ap_config.json pre-fill if a session is available.
//   4. LAUNCH: start the game via steam://rungameid/1942280.
//   5. PRE-FILL: write ap_config.json with the session's server/slot/password before
//      launch so the mod's in-game form is pre-populated.
//
//   * Sidecar: Games/ROMs/brotato/brotato_launcher.json
//
// ── BUILD NOTE ────────────────────────────────────────────────────────────────
//   This project sets UseWindowsForms=true alongside UseWPF=true, so WPF UI types
//   that also exist in WinForms (Color, Button, Brushes, MessageBox, FontWeights,
//   Orientation, …) are fully-qualified below to avoid CS0104 ambiguity.
//   No file-level "using X = System.Windows…" aliases are used (CS1537 guard).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class BrotatoPlugin : IGamePlugin
{
    // ── Constants ──────────────────────────────────────────────────────────────

    /// Brotato's Steam App ID.
    private const string BrotatoSteamAppId = "1942280";

    /// Steam "run game" URL.
    private static readonly string SteamRunUrl = $"steam://rungameid/{BrotatoSteamAppId}";

    /// The Steam Workshop placeholder folder ID required for local mod loading.
    /// (The "[Modders]" placeholder mod by Lex Looter, ID 3369699033.)
    private const string PlaceholderWorkshopId = "3369699033";

    /// The mod zip filename that must live inside the placeholder folder.
    private const string ModZipFileName = "RampagingHippy-Archipelago.zip";

    /// GitHub API — latest release for the mod.
    private const string GhReleasesApiUrl =
        "https://api.github.com/repos/SpenserHaddad/Brotato-ArchipelagoClient/releases/latest";

    /// GitHub releases page (for links).
    private const string GhReleasesUrl =
        "https://github.com/SpenserHaddad/Brotato-ArchipelagoClient/releases/latest";

    /// Steam Workshop link for subscribing to the Archipelago mod directly.
    private const string SteamWorkshopApUrl =
        "https://steamcommunity.com/app/1942280/workshop/";

    /// Steam Workshop link for the placeholder "[Modders]" mod.
    private const string PlaceholderWorkshopUrl =
        "https://steamcommunity.com/sharedfiles/filedetails/?id=3369699033";

    /// AP setup guide for Brotato.
    private const string SetupGuideUrl =
        "https://archipelago.gg/tutorial/Brotato/setup_en";

    /// Pinned fallback version (verified 2026-06-14).
    private const string FallbackVersion = "0.15.0";

    private static readonly string FallbackZipUrl =
        $"https://github.com/SpenserHaddad/Brotato-ArchipelagoClient/releases/download/v{FallbackVersion}/{ModZipFileName}";

    /// Godot user data path for Brotato's ModLoader config.
    /// ModLoaderConfig stores configs at user://mods-config/<mod_namespace-name>/<config_name>.json
    /// Godot "user://" maps to %APPDATA%\Godot\app_userdata\<game_name>\ on Windows.
    private static readonly string GodotUserDataBase =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Godot", "app_userdata", "Brotato");

    private static readonly string ModConfigDir =
        Path.Combine(GodotUserDataBase, "mods-config", "RampagingHippy-Archipelago");

    private static readonly string ApConfigPath =
        Path.Combine(ModConfigDir, "ap_config.json");

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders =
        {
            { "User-Agent", "Archipelago-Launcher/2.0" },
            { "Accept",     "application/vnd.github+json" },
        }
    };

    // ── IGamePlugin — Identity ─────────────────────────────────────────────────

    public string GameId      => "brotato";
    public string DisplayName => "Brotato";
    public string Subtitle    => "Native PC · Archipelago ModLoader mod";

    /// EXACT AP game string — verified against apworld/brotato/archipelago.json.
    public string ApWorldName => "Brotato";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "brotato.png");

    public string ThemeAccentColor => "#C0392B";   // Brotato's warm red palette

    public string[] GameBadges => new[] { "Steam · ModLoader mod" };

    public string Description =>
        "Brotato, the frantic top-down roguelike by Blobfish, played through the " +
        "Archipelago mod by Rampaging Hippy — a Godot ModLoader mod that turns the game " +
        "into an Archipelago multiworld client. Completing waves and collecting loot sends " +
        "checks across the multiworld; received items unlock characters, add gold, XP, and " +
        "shop slots. You bring your own copy of Brotato (Steam), the launcher installs the " +
        "Archipelago mod, and the game connects to the AP server in-game. The Xbox / Game " +
        "Pass version is NOT supported (no ModLoader).";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ──────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" = the mod zip is present in the placeholder Workshop folder.
    public bool IsInstalled => FindInstalledModZip() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ──────────────────────────────────────────────────────────────────

    /// Working directory for downloads and bookkeeping.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Brotato");

    private string RomLibraryDirectory
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);

    private string SettingsSidecarPath
        => Path.Combine(RomLibraryDirectory, "brotato_launcher.json");

    // ── Internal state ─────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ───────────────────────────────────────────────────────
    // ConnectsItself = true: the mod owns the slot; launcher does not hold an ApClient.
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── Lifecycle — CheckForUpdate ─────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            InstalledVersion = FindInstalledModZip() != null
                ? (ReadStampedVersion() ?? "installed")
                : null;
        }
        catch { InstalledVersion = null; }
            try
        {
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync("SpenserHaddad", "Brotato-ArchipelagoClient", ct));
        }
        catch
        {
            AvailableVersion = null; // contract: never throw on network failure
        }
    }

    // ── Lifecycle — InstallOrUpdate ────────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 1. Locate the Steam Workshop content folder for Brotato's placeholder mod.
        progress.Report((3, "Locating Steam Workshop content folder..."));
        string? workshopFolder = FindBrotatoWorkshopRoot();
        if (workshopFolder == null)
        {
            throw new InvalidOperationException(
                "Could not find the Steam Workshop content folder for Brotato " +
                $"(steamapps\\workshop\\content\\{BrotatoSteamAppId}). " +
                "Make sure Steam is installed and Brotato is installed on Steam. " +
                "The Xbox / Game Pass version of Brotato is NOT supported.");
        }

        // 2. Check that the placeholder mod folder exists (Workshop ID 3369699033).
        //    The user must subscribe to the "[Modders]" placeholder mod via Steam so
        //    that the Workshop downloads it. We cannot do this step automatically.
        string placeholderFolder = Path.Combine(workshopFolder, PlaceholderWorkshopId);
        if (!Directory.Exists(placeholderFolder))
        {
            throw new InvalidOperationException(
                "The placeholder mod folder is missing. Before installing, you must " +
                "subscribe to the \"[Modders]\" placeholder Workshop mod (ID 3369699033) " +
                "in Steam so it downloads to your Workshop folder. " +
                "See the Settings panel for a direct link. " +
                $"Expected folder: {placeholderFolder}");
        }

        // 3. Resolve latest release.
        progress.Report((10, "Checking for the latest Brotato Archipelago mod release..."));
        var (version, zipUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        if (string.IsNullOrWhiteSpace(zipUrl))
        {
            throw new InvalidOperationException(
                "Could not resolve the mod download URL. Check your internet connection, " +
                "or manually download the zip from: " + GhReleasesUrl);
        }

        // 4. Download the zip.
        string destZip = Path.Combine(placeholderFolder, ModZipFileName);
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"brotato-ap-{version}-{Guid.NewGuid():N}.zip");

        try
        {
            progress.Report((15, $"Downloading {ModZipFileName} v{version}..."));
            using (var response = await _http.GetAsync(
                zipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                long total      = response.Content.Headers.ContentLength ?? -1;
                long downloaded = 0;

                await using var src = await response.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tempZip);
                var buf = new byte[81920];
                int read;
                while ((read = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, read), ct);
                    downloaded += read;
                    if (total > 0)
                    {
                        int pct = (int)(15 + 70 * downloaded / total);
                        progress.Report((pct, $"Downloading... {downloaded / 1024}KB / {total / 1024}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            // 5. Move the downloaded zip into the placeholder folder (un-extracted).
            //    DO NOT unzip — ModLoader expects the zip file as-is.
            progress.Report((88, "Installing mod zip into Workshop placeholder folder..."));
            File.Copy(tempZip, destZip, overwrite: true);

            WriteStampedVersion(version);
            InstalledVersion = version;

            progress.Report((100,
                $"Installed {ModZipFileName} v{version} successfully. " +
                "Launch Brotato and use the \"Archipelago\" button on the main menu to connect. " +
                "The launcher will pre-fill your server, slot, and password in the in-game menu."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    // ── Lifecycle — Verify ─────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // ── Lifecycle — Launch ─────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // Pre-fill the AP connection config so the mod's in-game form is populated.
        if (session != null)
            WriteApConfig(session.ServerUri, session.SlotName, session.Password);

        StartBrotato();
        return Task.CompletedTask;
    }

    public bool ConnectsItself    => true;
    public bool SupportsStandalone => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartBrotato();
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

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var accent  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC0, 0x39, 0x2B));

        var panel = new System.Windows.Controls.StackPanel
            { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Detect install state ───────────────────────────────────────────
        string? steamBrotato      = DetectSteamBrotatoDir();
        string? workshopRoot      = FindBrotatoWorkshopRoot();
        string? placeholderFolder = workshopRoot != null
            ? Path.Combine(workshopRoot, PlaceholderWorkshopId)
            : null;
        bool placeholderExists    = placeholderFolder != null && Directory.Exists(placeholderFolder);
        string? modZip            = FindInstalledModZip();
        string? overrideDir       = LoadOverrideDir();

        // ── Overview blurb ─────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "Brotato is your own game (Steam) with the Archipelago mod installed on top via " +
                "Brotato's ModLoader. Due to a Steam update, mods must come from the Workshop: the " +
                "standard method is to place the mod zip inside the \"[Modders]\" placeholder Workshop " +
                "folder (see the steps below). The launcher handles the download and placement. " +
                "Connection info is pre-filled into the mod's config file before launch; you can " +
                "still adjust it in-game via the Archipelago menu on the title screen.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: Brotato install ───────────────────────────────────────
        AddSectionHeader(panel, "BROTATO INSTALL", muted);

        string steamMsg = steamBrotato != null
            ? "Detected: " + steamBrotato
            : "Brotato Steam installation not detected. Make sure Brotato is installed via Steam.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = steamMsg, FontSize = 11,
            Foreground = steamBrotato != null ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        // Override folder row
        var overrideRow = new System.Windows.Controls.DockPanel
            { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var overrideBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? steamBrotato ?? "",
            IsReadOnly = true, FontSize = 11,
            Margin      = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Brotato install folder. Detected from Steam automatically; " +
                          "use this picker for a non-standard or GOG install.",
        };
        var overrideBtn = new System.Windows.Controls.Button
        {
            Content     = "Select folder...", Width = 120,
            Padding     = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        overrideBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Brotato install folder",
                InitialDirectory = Directory.Exists(overrideDir ?? steamBrotato ?? "")
                                   ? (overrideDir ?? steamBrotato!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                if (!LooksLikeBrotatoDir(picked))
                {
                    System.Windows.MessageBox.Show(
                        "That does not look like a Brotato install folder. " +
                        "Pick the folder containing \"Brotato.exe\" " +
                        @"(usually ...\steamapps\common\Brotato).",
                        "Not a Brotato folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                SaveOverrideDir(picked);
                overrideBox.Text = picked;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(overrideBtn, System.Windows.Controls.Dock.Right);
        overrideRow.Children.Add(overrideBtn);
        overrideRow.Children.Add(overrideBox);
        panel.Children.Add(overrideRow);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Steam installs are detected automatically (appid 1942280). Use the picker " +
                   "for GOG or a non-standard Steam library.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 4, 0, 12),
        });

        // ── Section: Mod install status ────────────────────────────────────
        AddSectionHeader(panel, "ARCHIPELAGO MOD STATUS", muted);

        // Placeholder folder check
        string placeholderMsg;
        System.Windows.Media.Brush placeholderColor;
        if (workshopRoot == null)
        {
            placeholderMsg   = "Steam Workshop folder not found. Install Brotato via Steam.";
            placeholderColor = warn;
        }
        else if (!placeholderExists)
        {
            placeholderMsg =
                "Placeholder mod folder not found. You must subscribe to the " +
                "\"[Modders]\" Workshop mod (ID 3369699033) via Steam first — " +
                "see the guided steps below and the link in the Links section.";
            placeholderColor = warn;
        }
        else
        {
            placeholderMsg   = "Placeholder Workshop folder found: " + placeholderFolder;
            placeholderColor = success;
        }
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = placeholderMsg, FontSize = 11, Foreground = placeholderColor,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        string modMsg = modZip != null
            ? "Archipelago mod zip found: " + modZip
            : "Archipelago mod zip not found. Use the Install button on the Play tab " +
              "(requires the placeholder folder above).";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modMsg, FontSize = 11,
            Foreground = modZip != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        // Config pre-fill status
        bool configExists = File.Exists(ApConfigPath);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = configExists
                ? "AP connection config found: " + ApConfigPath
                : "AP connection config not yet written (will be created on first Launch with AP session).",
            FontSize = 11, Foreground = configExists ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Connection (pre-fill) ─────────────────────────────────
        AddSectionHeader(panel, "CONNECTION (AUTO PRE-FILLED BEFORE LAUNCH)", muted);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "When you click Play with an active Archipelago session, the launcher writes " +
                $"the server, slot name, and password to:\n{ApConfigPath}\n" +
                "The mod reads this file on startup, so the in-game \"Archipelago\" menu is " +
                "pre-populated. You can still edit the fields in-game before connecting.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ────────────────────────────────────
        AddSectionHeader(panel, "GUIDED SETUP (first time)", muted);
        foreach (string step in new[]
        {
            "1. Own Brotato on Steam. Install it if you have not. " +
               "(GOG/Epic also work but need the folder override above.)",
            "2. In Steam, go to the Brotato Workshop and subscribe to the \"[Modders]\" " +
               "placeholder mod (Workshop ID 3369699033) — see the link below. " +
               "Steam will download a small placeholder folder.",
            "3. Click the Install button on the Play tab. The launcher downloads " +
               "RampagingHippy-Archipelago.zip and places it (un-extracted) in the " +
               "placeholder folder. This is everything the game needs.",
            "4. Alternatively: subscribe to the official Archipelago mod on the " +
               "Steam Workshop (search \"Archipelago\" in Brotato's Workshop). " +
               "It auto-updates, but requires the Steam Workshop install path, and " +
               "you would still need to subscribe first.",
            "5. Launch Brotato. The main menu has an \"Archipelago\" button above " +
               "\"New Game\". Press it to open the connection form. " +
               "When launched via this launcher with an AP session active, the form " +
               "is pre-filled with your server, slot, and password.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ──────────────────────────────────────────────────────────
        AddSectionHeader(panel, "LINKS", muted);
        foreach (var (label, url) in new[]
        {
            ("[Modders] placeholder Workshop mod (subscribe first) ↗", PlaceholderWorkshopUrl),
            ("Brotato Steam Workshop (search Archipelago mod) ↗",      SteamWorkshopApUrl),
            ("Brotato AP GitHub releases (manual download) ↗",         GhReleasesUrl),
            ("Archipelago Brotato setup guide ↗",                      SetupGuideUrl),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content             = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding             = new System.Windows.Thickness(0, 2, 0, 2),
                Background          = System.Windows.Media.Brushes.Transparent,
                BorderThickness     = new System.Windows.Thickness(0),
                FontSize            = 12,
                Margin              = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground          = new System.Windows.Media.SolidColorBrush(
                                          System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor              = System.Windows.Input.Cursors.Hand,
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
        try
        {
            string json = await _http.GetStringAsync(
                "https://api.github.com/repos/SpenserHaddad/Brotato-ArchipelagoClient/releases",
                ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                string tag  = el.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
                string body = el.TryGetProperty("body",     out var b) ? (b.GetString() ?? "") : "";
                string url  = el.TryGetProperty("html_url", out var u) ? (u.GetString() ?? GhReleasesUrl) : GhReleasesUrl;

                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                items.Add(new NewsItem(
                    Title:   "Brotato AP " + tag,
                    Body:    body,
                    Version: tag.TrimStart('v'),
                    Date:    date,
                    Url:     url));

                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — AP connection config ─────────────────────────────────

    /// Write the Godot ModLoader AP config file so the in-game form is pre-populated.
    /// Format matches the manifest.json config_schema (ap_server, ap_player, ap_password).
    private static void WriteApConfig(string serverUri, string slotName, string? password)
    {
        try
        {
            Directory.CreateDirectory(ModConfigDir);

            // Parse server URI: may be "archipelago.gg:38281" or "ws://host:port"
            string serverHost = serverUri ?? "archipelago.gg";
            if (serverHost.StartsWith("ws://",  StringComparison.OrdinalIgnoreCase))
                serverHost = serverHost[5..];
            if (serverHost.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
                serverHost = serverHost[6..];

            var config = new Dictionary<string, object?>
            {
                ["ap_server"]     = serverHost,
                ["ap_player"]     = slotName ?? "",
                ["ap_password"]   = password ?? "",
                ["has_saved_run"] = false,
                ["deathlink_mode"] = 0,
            };

            string json = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(ApConfigPath, json, new UTF8Encoding(false));
        }
        catch { /* non-fatal — user can fill in-game */ }
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    /// Resolve the latest GitHub release: tag + the RampagingHippy-Archipelago.zip URL.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestReleaseAsync(
        CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GhReleasesApiUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
            string ver = tag.TrimStart('v');

            if (root.TryGetProperty("assets", out var assets) &&
                assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string? name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (!string.Equals(name, ModZipFileName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string? url = asset.TryGetProperty("browser_download_url", out var u)
                                  ? u.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(url))
                        return (ver, url);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* offline — pinned fallback */ }

        return (FallbackVersion, FallbackZipUrl);
    }

    // ── Private helpers — Steam / Brotato detection ────────────────────────────

    /// Full path to the Brotato install to use: override (if set) else Steam-detected.
    private string? ResolveBrotatoDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeBrotatoDir(ov)) return ov;
        try { return DetectSteamBrotatoDir(); } catch { return null; }
    }

    private static bool LooksLikeBrotatoDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, "Brotato.exe"));
        }
        catch { return false; }
    }

    /// Detect Brotato's Steam install directory via registry + libraryfolders.vdf.
    private static string? DetectSteamBrotatoDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{BrotatoSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeBrotatoDir(candidate)) return candidate;
                    }
                    string conv = Path.Combine(common, "Brotato");
                    if (LooksLikeBrotatoDir(conv)) return conv;
                }
                catch { }
            }
        }
        return null;
    }

    /// Find the Steam Workshop root for Brotato:
    /// ...\steamapps\workshop\content\1942280
    private static string? FindBrotatoWorkshopRoot()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                string workshopPath = Path.Combine(
                    lib, "steamapps", "workshop", "content", BrotatoSteamAppId);
                if (Directory.Exists(workshopPath))
                    return workshopPath;
            }
        }
        return null;
    }

    /// Find the installed mod zip path (if present).
    private static string? FindInstalledModZip()
    {
        string? workshopRoot = FindBrotatoWorkshopRoot();
        if (workshopRoot == null) return null;

        string zipPath = Path.Combine(workshopRoot, PlaceholderWorkshopId, ModZipFileName);
        return File.Exists(zipPath) ? zipPath : null;
    }

    // ── Private helpers — launch ───────────────────────────────────────────────

    private void StartBrotato()
    {
        string? brotatoDir = ResolveBrotatoDir();
        string? exe        = brotatoDir != null ? Path.Combine(brotatoDir, "Brotato.exe") : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = brotatoDir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Brotato.exe.");

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

        // Fall back to Steam URL.
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
            "Could not find Brotato.exe. Open Settings and pick your Brotato install folder, " +
            "or install Brotato via Steam. The Xbox / Game Pass version is not supported.",
            "Brotato.exe");
    }

    // ── Private helpers — Steam root / library enumeration ────────────────────

    private static IEnumerable<string> SteamRoots()
    {
        string? hkcu = ReadRegistryString(Registry.CurrentUser,
            @"Software\Valve\Steam", "SteamPath");
        if (!string.IsNullOrWhiteSpace(hkcu)) yield return NormalizeSteamPath(hkcu);

        string? hklm = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm)) yield return NormalizeSteamPath(hklm);

        string? hklm2 = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm2)) yield return NormalizeSteamPath(hklm2);

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
        try { text = File.ReadAllText(vdf); } catch { yield break; }

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

    // ── Private helpers — settings sidecar ────────────────────────────────────

    private sealed class BrotatoSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private BrotatoSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<BrotatoSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(BrotatoSettings s)
    {
        try
        {
            Directory.CreateDirectory(RomLibraryDirectory);
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

    // ── Private helpers — UI utilities ────────────────────────────────────────

    private static void AddSectionHeader(
        System.Windows.Controls.StackPanel panel,
        string title,
        System.Windows.Media.Brush color)
    {
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = title,
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = color,
            Margin     = new System.Windows.Thickness(0, 8, 0, 8),
        });
    }
}
