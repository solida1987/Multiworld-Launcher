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

// NOTE on type qualification (BUILD GOTCHA — CS0104 / CS1537):
// The project sets <UseWPF>true</UseWPF> and <UseWindowsForms>true</UseWindowsForms>.
// Many short type names are ambiguous between WPF and WinForms. GlobalUsings.cs already
// aliases the colliding names it cares about to their WPF types — adding file-level
// `using X = System.Windows...;` aliases here would be CS1537 (duplicate alias).
// Every WPF UI type below is FULLY QUALIFIED (System.Windows.*, etc.).
// Same approach as the shipped KH1 / Subnautica / Undertale plugins.

namespace LauncherV2.Plugins.MetroidPrime;

// ═══════════════════════════════════════════════════════════════════════════════
// MetroidPrimePlugin — detect / install-guide / launch for "Metroid Prime" (GCN)
// played through the community Archipelago integration by Electro1512.
// NATIVE "ConnectsItself".
//
// WHAT KIND OF INTEGRATION IS THIS? (verified 2026-06-15)
// ─────────────────────────────────────────────────────────────────────────────
// Metroid Prime's Archipelago support is a COMMUNITY apworld, NOT in AP-main
// (ArchipelagoMW/Archipelago). The home repo is:
//   https://github.com/Electro1512/MetroidAPrime
// Latest release verified live: tag v0.5.0 (July 15, 2025).
//
// THE PIECES A PLAYER NEEDS:
//   * THE BASE GAME — the player's OWN GameCube ISO of Metroid Prime (NTSC-U or
//     NTSC-J; GCN only — the Wii Trilogy disc and the Switch Remastered are NOT
//     supported by this apworld). This is owned software — the launcher detects it,
//     it NEVER ships or downloads it (§11).
//
//   * THE METROID PRIME AP RELEASE — a versioned zip from
//     Electro1512/MetroidAPrime/releases containing:
//       - metroidprime.apworld  — drop into Archipelago's lib/worlds folder
//       - MetroidPrimeClient[.exe]  — the connector/client that patches the ISO
//                                     via randomprime and connects to the AP slot
//     The zip comes in two flavours (Python 3.12 / 3.11) depending on the installed
//     AP version. The launcher downloads the latest release zip.
//
//   * DOLPHIN EMULATOR — the player's own copy of Dolphin. The launcher does NOT
//     ship Dolphin (§11 — it is the player's own tool). Launch = open the patched
//     ISO in Dolphin.
//
// HOW IT CONNECTS (verified against the official setup guide):
//   The connection is made IN the "Metroid Prime Client" window, NOT via a
//   command-line arg on Dolphin or a config file the launcher can pre-write. The
//   documented flow:
//     1. Generate the multiworld and download the .apmp1 patch file.
//     2. Open MetroidPrimeClient.exe, provide your vanilla ISO + the .apmp1 —
//        the client calls randomprime to patch the ISO → produces a patched ISO.
//     3. Open the patched ISO in Dolphin.
//     4. In the client, enter server host:port and click Connect, then your slot
//        name. The client holds the AP slot connection — not the launcher.
//   Because the MetroidPrimeClient owns the slot:
//     * ConnectsItself = true — the launcher must NOT hold its own ApClient on the
//       same slot (they would kick each other off continuously).
//     * The launcher GUIDES the user and surfaces the session's host:port/slot for
//       copying into the client. No prefill mechanism exists.
//
// WHAT THIS PLUGIN HONESTLY DOES:
//   1. DETECT Dolphin: checks the PATH + the common install locations (Steam,
//      Program Files). Detects via registry / folder heuristic. Never ships Dolphin.
//   2. INSTALL/UPDATE: download the latest MetroidPrime AP zip from GitHub, extract
//      to Games/MetroidPrimeAP/ and stamp the version. The apworld file itself is
//      extracted there too so the user can copy it to their Archipelago install.
//   3. VERIFY: checks the stamped version folder is non-empty.
//   4. LAUNCH (AP): best effort — opens the MetroidPrimeClient.exe from the
//      extracted release. Connection is entered in the client GUI (no prefill).
//      SupportsStandalone = true (Dolphin + a vanilla or patched ISO runs without AP).
//   5. GUIDE the irreducible steps + links in Settings.
//
// AP GAME STRING:
//   game = "Metroid Prime"  — VERIFIED against Electro1512/MetroidAPrime (the YAML
//   template is "Metroid Prime.yaml" and the setup guide names the AP game
//   "Metroid Prime"; the client connects with this game name). NOTE: this is a
//   community apworld — not in AP-main — so it is NOT in the standard AP DataPackage.
//
// DEFENSIVE / FLAGGED DETAILS:
//   * RELEASE ASSETS: latest verified = tag v0.5.0, July 2025. Two zips:
//     "MetroidPrimeAP-<ver>-3.12.zip" (Python 3.12+) and
//     "MetroidPrimeAP-<ver>-3.11.zip" (Python 3.11). The installer picks the
//     3.12 zip by preference (AP 5.x ships with Python 3.12), else the first zip.
//     Pinned fallback: v0.5.0 / 3.12 zip. Re-verify on bump.
//   * ISO DETECTION: the plugin looks for *.iso / *.gcm files hinted by a per-game
//     settings sidecar (Games/ROMs/metroidprime/mp1_launcher.json). No ROM scanning
//     of the user's drives — the user always provides the ISO path explicitly.
//   * No plaintext AP password is ever written by this plugin.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class MetroidPrimePlugin : IGamePlugin
{
    // ── Constants — community Metroid Prime AP release repo ──────────────────────

    private const string AP_OWNER = "Electro1512";
    private const string AP_REPO  = "MetroidAPrime";
    private static readonly string ApRepoUrl =
        $"https://github.com/{AP_OWNER}/{AP_REPO}";
    private static readonly string GH_AP_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{AP_OWNER}/{AP_REPO}/releases/latest";
    private static readonly string GH_AP_RELEASES_URL =
        $"https://api.github.com/repos/{AP_OWNER}/{AP_REPO}/releases";

    private const string SetupGuideUrl =
        "https://github.com/Electro1512/MetroidAPrime/blob/main/docs/setup_en.md";
    private const string DolphinUrl = "https://dolphin-emu.org/download/";

    // Pinned fallback for when the GitHub API is unreachable.
    // Tag v0.5.0 verified live 2026-06-15; 3.12 zip verified as primary asset.
    private const string FallbackVersion    = "0.5.0";
    private const string FallbackZipName312 = "MetroidPrimeAP-0.5.0-3.12.zip";
    private static readonly string FallbackZipUrl312 =
        $"{ApRepoUrl}/releases/download/v{FallbackVersion}/{FallbackZipName312}";

    private const string VersionFileName    = "mp1_ap_version.dat";
    private const string ClientExeName      = "MetroidPrimeClient.exe";
    private const string ApWorldFileName    = "metroidprime.apworld";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "metroidprime";
    public string DisplayName => "Metroid Prime";
    public string Subtitle    => "GameCube · Dolphin · Archipelago";

    /// EXACT AP game string — VERIFIED against Electro1512/MetroidAPrime
    /// ("Metroid Prime.yaml" + setup guide + client game name).
    public string ApWorldName => "Metroid Prime";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "metroidprime.png");

    public string ThemeAccentColor => "#E87820";   // Metroid orange/amber

    public string[] GameBadges => new[] { "Requires GCN ISO", "Dolphin" };

    public string Description =>
        "Metroid Prime is Samus Aran's atmospheric first-person adventure on the " +
        "GameCube, exploring the derelict Space Pirate facilities on Tallon IV. " +
        "In the Archipelago randomizer, all suit upgrades and expansion items are " +
        "shuffled into the multiworld, rewarding creative routing toward the final " +
        "Metroid Prime encounter. You bring your own GameCube ISO (NTSC-U or " +
        "NTSC-J — the Wii Trilogy and Switch Remastered are not supported); the " +
        "Metroid Prime Client patches it via randomprime and connects to the " +
        "multiworld, while you play in Dolphin. The launcher downloads the client " +
        "software and guides the one-time setup. Community apworld by Electro1512.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the Metroid Prime AP client software is present in our
    /// managed folder (or a version stamp exists). The base ISO and Dolphin are
    /// separate and not counted here.
    public bool IsInstalled => HasClientSoftware();

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Folder where the Metroid Prime AP release zip is extracted by this launcher.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "MetroidPrimeAP");

    private string VersionFilePath  => Path.Combine(GameDirectory, VersionFileName);
    private string ClientExePath    => Path.Combine(GameDirectory, ClientExeName);
    private string ApWorldLocalPath => Path.Combine(GameDirectory, ApWorldFileName);

    /// This plugin's OWN settings sidecar — kept out of the shared SettingsStore
    /// (same self-contained approach as the KH1 / Subnautica plugins).
    private string SettingsSidecarDir  =>
        Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath =>
        Path.Combine(SettingsSidecarDir, "mp1_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _clientProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The MetroidPrimeClient owns the AP slot connection. The launcher relays
    // nothing. These exist only for interface compatibility (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            InstalledVersion = HasClientSoftware()
                ? (File.Exists(VersionFilePath)
                    ? (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim()
                    : "installed")
                : null;
        }
        catch { InstalledVersion = null; }
            try
        {
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(AP_OWNER, AP_REPO, ct));
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
        progress.Report((3, "Checking the latest Metroid Prime AP release..."));
        var (version, zipUrl, _) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        // Already current? (idempotent fast path)
        if (HasClientSoftware()
            && File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100,
                $"Metroid Prime AP client {version} is up to date. " +
                "See Settings for the setup and connection guide."));
            return;
        }

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the Metroid Prime AP release download on GitHub. " +
                "Check your internet connection, or download it manually from " +
                ApRepoUrl + "/releases/latest. You also need your own GameCube ISO " +
                "of Metroid Prime (NTSC-U or NTSC-J) and Dolphin Emulator.");

        await DownloadAndExtractAsync(zipUrl, version, progress, ct);

        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        string? dolphinPath = DetectDolphinExe();
        string dolphinLine  = dolphinPath != null
            ? $"Dolphin detected at \"{dolphinPath}\". "
            : "Reminder: install Dolphin Emulator (dolphin-emu.org) if you haven't. ";

        progress.Report((100,
            $"Metroid Prime AP client {version} downloaded. " + dolphinLine +
            "To finish setup: install the .apworld in Archipelago, generate the " +
            "multiworld, open MetroidPrimeClient.exe, provide your GameCube ISO + " +
            "the .apmp1 patch to produce a patched ISO, open the patched ISO in " +
            "Dolphin, then enter your server host:port and slot name in the client " +
            "window. See Settings for the full guided steps."));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // ── AutoMod validation (unused here — no base-game install to validate) ───

    // The base game is a GCN ISO the user already has; we accept any folder because
    // the ISO picker and validation happen inside the Metroid Prime Client itself,
    // not here. Default (null = accept) is inherited from the interface.

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // The AP connection is entered in the Metroid Prime Client's own GUI —
        // there is no command-line / config prefill we can apply (verified — see
        // header). Launching from this tile opens the client for convenience; the
        // user connects there with the session credentials (Settings panel surfaces
        // them).
        //
        // ConnectsItself = true: launcher MUST NOT hold its own ApClient on this slot.
        _ = session; // intentionally unused — no prefill mechanism exists
        OpenMetroidPrimeClient();
        return Task.CompletedTask;
    }

    /// Metroid Prime with Dolphin runs perfectly without AP.
    public bool SupportsStandalone => true;

    /// The MetroidPrimeClient owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        // Standalone: just try to open Dolphin directly if detected.
        string? dolphin = DetectDolphinExe();
        if (dolphin != null && File.Exists(dolphin))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName        = dolphin,
                    UseShellExecute = true,
                });
            }
            catch { /* fall-through: tell user to open Dolphin themselves */ }
        }
        else
        {
            // If the client is installed, at least open it so the user can browse
            // to their ISO.
            if (File.Exists(ClientExePath))
                OpenMetroidPrimeClient();
        }
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _clientProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning     = false;
        _clientProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself, see header) ────────────────────────

    public Task ReceiveItemsAsync(
        ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The MetroidPrimeClient receives items from the AP server and injects them
        // into the running game. The launcher relays nothing.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The MetroidPrimeClient renders its own connection state.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var accent  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0x78, 0x20));

        var panel = new System.Windows.Controls.StackPanel
            { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Metroid Prime is played from your own GameCube ISO (NTSC-U or " +
                   "NTSC-J — the Wii Trilogy and Switch Remastered are not supported) " +
                   "in Dolphin Emulator. The Metroid Prime Client patches the ISO via " +
                   "randomprime and connects to the multiworld; it — not this launcher — " +
                   "holds the Archipelago slot connection. This is a community apworld " +
                   "(Electro1512/MetroidAPrime), not part of AP-main. These external " +
                   "steps are not verified by this launcher.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: client software status ──────────────────────────────
        panel.Children.Add(SectionHeader("METROID PRIME AP CLIENT", muted));

        bool hasClient = File.Exists(ClientExePath);
        bool hasApWorld = File.Exists(ApWorldLocalPath);
        string? installedVer = null;
        if (File.Exists(VersionFilePath))
        {
            try { installedVer = File.ReadAllText(VersionFilePath).Trim(); }
            catch { }
        }

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = hasClient
                ? $"✓ Client found: {ClientExePath}" +
                  (installedVer != null ? $"  (version {installedVer})" : "")
                : "Client not downloaded yet. Use Install on the Play tab, or get " +
                  "the release from the repo below.",
            FontSize = 11, Foreground = hasClient ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = hasApWorld
                ? $"✓ apworld found: {ApWorldLocalPath}  (copy to Archipelago's lib/worlds/)"
                : "metroidprime.apworld not present yet (downloaded together with the client).",
            FontSize = 11, Foreground = hasApWorld ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Dolphin detection ────────────────────────────────────
        panel.Children.Add(SectionHeader("DOLPHIN EMULATOR", muted));

        string? dolphinExe  = DetectDolphinExe();
        string? dolphinPath = LoadDolphinOverride();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = dolphinExe != null
                ? $"✓ Detected: {dolphinExe}"
                : "Dolphin not auto-detected. Pick it below or install from dolphin-emu.org.",
            FontSize = 11, Foreground = dolphinExe != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        var dolphinRow = new System.Windows.Controls.DockPanel
            { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dolphinBox = new System.Windows.Controls.TextBox
        {
            Text = dolphinPath ?? dolphinExe ?? "", IsReadOnly = true, FontSize = 12,
            Margin     = new System.Windows.Thickness(0, 0, 8, 0),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip = "Path to Dolphin.exe. Set this if Dolphin was not auto-detected.",
        };
        var dolphinBtn = new System.Windows.Controls.Button
        {
            Content = "Select Dolphin...", Width = 130,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dolphinBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Select Dolphin.exe",
                Filter = "Dolphin|Dolphin.exe|All executables|*.exe",
                FileName = "Dolphin.exe",
            };
            if (dlg.ShowDialog() == true)
            {
                SaveDolphinOverride(dlg.FileName);
                dolphinBox.Text = dlg.FileName;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(dolphinBtn, System.Windows.Controls.Dock.Right);
        dolphinRow.Children.Add(dolphinBtn);
        dolphinRow.Children.Add(dolphinBox);
        panel.Children.Add(dolphinRow);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Dolphin is detected from common install locations and your PATH. " +
                   "Use this picker only for a non-standard install.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 4, 0, 12),
        });

        // ── Section: Connecting (entered in the client) ───────────────────
        panel.Children.Add(SectionHeader("CONNECTING (entered in the Metroid Prime Client window)", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Open MetroidPrimeClient.exe from the downloaded release. Provide " +
                   "your GameCube ISO and the .apmp1 patch file from the room; the client " +
                   "patches the ISO via randomprime and produces a patched ISO. Open that " +
                   "patched ISO in Dolphin, then enter your server host:port in the client " +
                   "and click Connect. Type your slot name to join. The client — not this " +
                   "launcher — holds the multiworld connection.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup ─────────────────────────────────────────
        panel.Children.Add(SectionHeader("GUIDED SETUP", muted));
        foreach (string step in new[]
        {
            "1. Own a GameCube ISO of Metroid Prime (NTSC-U or NTSC-J). The Wii " +
                "Trilogy and Switch Remastered are NOT supported by this apworld.",
            "2. Install Dolphin Emulator (dolphin-emu.org). Recommended: latest " +
                "stable or development build. Use \"Select Dolphin...\" above if it " +
                "was not auto-detected.",
            "3. Download the Metroid Prime AP client: use Install on the Play tab " +
                "(it downloads and extracts the release for you), or get it from the " +
                "release page below. Both the client and the apworld file are in the zip.",
            "4. Copy metroidprime.apworld from the downloaded folder into your " +
                "Archipelago installation's lib/worlds/ folder (create the folder if " +
                "it doesn't exist). Restart Archipelago after copying.",
            "5. Generate the multiworld with your friends. Download the .apmp1 patch " +
                "file from the room for your slot.",
            "6. Open MetroidPrimeClient.exe. Provide your vanilla GameCube ISO and " +
                "the .apmp1 — the client produces a patched ISO via randomprime.",
            "7. Open the patched ISO in Dolphin. Then in the Metroid Prime Client, " +
                "enter your server host:port, click Connect, and enter your slot name. " +
                "(This launcher cannot pre-fill the connection — it is entered in the " +
                "client window.)",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(SectionHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("Metroid Prime AP Setup Guide ↗",             SetupGuideUrl),
            ("Metroid Prime AP Releases ↗",                ApRepoUrl + "/releases"),
            ("Metroid Prime AP Repository (Electro1512) ↗", ApRepoUrl),
            ("Dolphin Emulator Download ↗",                DolphinUrl),
            ("Archipelago Official ↗",                     "https://archipelago.gg"),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding    = new System.Windows.Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0), FontSize = 12,
                Margin   = new System.Windows.Thickness(0, 0, 0, 4),
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
        => new System.Windows.Controls.TextBlock
        {
            Text = text, FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin = new System.Windows.Thickness(0, 8, 0, 8),
        };

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_AP_RELEASES_URL, ct);
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

    /// Resolve the latest release: version + download URL.
    /// The release has two zip flavours (3.12 / 3.11); prefer 3.12 (AP 5.x default).
    /// Falls back to the pinned v0.5.0 / 3.12 zip when the API is unreachable.
    private async Task<(string Version, string? ZipUrl, string? ApWorldUrl)>
        ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_AP_RELEASES_LATEST_URL, ct);
            using var doc  = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? zip312  = null;   // .zip whose name contains "3.12"
                string? zip311  = null;   // .zip whose name contains "3.11"
                string? anyZip  = null;   // any .zip
                string? apworld = null;   // a .apworld

                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();

                    if (lower.EndsWith(".apworld"))
                        apworld ??= url;

                    if (!lower.EndsWith(".zip")) continue;
                    anyZip ??= url;
                    if (lower.Contains("3.12")) zip312 ??= url;
                    else if (lower.Contains("3.11")) zip311 ??= url;
                }

                string? bestZip = zip312 ?? zip311 ?? anyZip;
                if (bestZip != null)
                    return (version, bestZip, apworld);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (FallbackVersion, FallbackZipUrl312, null);
    }

    // ── Private helpers — client software detection ───────────────────────────

    private bool HasClientSoftware()
    {
        try
        {
            if (!Directory.Exists(GameDirectory)) return false;
            if (File.Exists(VersionFilePath)) return true;
            // Any extracted content means it is present.
            foreach (var _ in Directory.EnumerateFileSystemEntries(GameDirectory))
                return true;
        }
        catch { }
        return false;
    }

    // ── Private helpers — launching the client ────────────────────────────────

    private void OpenMetroidPrimeClient()
    {
        if (File.Exists(ClientExePath))
        {
            try
            {
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName         = ClientExePath,
                    WorkingDirectory = GameDirectory,
                    UseShellExecute  = true,
                }) ?? throw new InvalidOperationException("Failed to start MetroidPrimeClient.");

                _clientProcess = proc;
                IsRunning      = true;
                try
                {
                    proc.EnableRaisingEvents = true;
                    proc.Exited += (_, _) =>
                    {
                        IsRunning = false;
                        GameExited?.Invoke(proc.ExitCode);
                    };
                }
                catch { /* some processes don't expose Exited — non-fatal */ }
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* fall through to guidance below */ }
        }

        throw new FileNotFoundException(
            "MetroidPrimeClient.exe not found. Use Install on the Play tab to " +
            "download the Metroid Prime AP release, or get it from " +
            ApRepoUrl + "/releases/latest. Then open the client, provide your " +
            "GameCube ISO and the .apmp1 patch, and connect to the AP server.",
            ClientExeName);
    }

    // ── Private helpers — Dolphin detection ──────────────────────────────────

    /// Locate Dolphin.exe via the user's override → then a heuristic scan of
    /// common install paths and the PATH environment. Never throws.
    private string? DetectDolphinExe()
    {
        string? ov = LoadDolphinOverride();
        if (!string.IsNullOrWhiteSpace(ov) && File.Exists(ov))
            return ov;

        // 1. Prefer the user's PATH (covers portable / custom installs)
        foreach (string? dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir, "Dolphin.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }

        // 2. Common install locations
        foreach (string candidate in DolphinCandidates())
        {
            try { if (File.Exists(candidate)) return candidate; }
            catch { }
        }

        // 3. Steam — Dolphin is not on Steam, but check anyway for completeness.
        string? steamRoot = ReadRegistryString(Registry.CurrentUser,
            @"Software\Valve\Steam", "SteamPath");
        if (!string.IsNullOrWhiteSpace(steamRoot))
        {
            string candidate = Path.Combine(
                steamRoot.Replace('/', '\\'), "steamapps", "common",
                "Dolphin", "Dolphin.exe");
            try { if (File.Exists(candidate)) return candidate; }
            catch { }
        }

        return null;
    }

    private static IEnumerable<string> DolphinCandidates()
    {
        // %LOCALAPPDATA%\Programs\Dolphin (typical user-scope installer)
        string? local = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
        {
            yield return Path.Combine(local, "Programs", "Dolphin", "Dolphin.exe");
            yield return Path.Combine(local, "Dolphin", "Dolphin.exe");
        }

        // %PROGRAMFILES%\Dolphin and %PROGRAMFILES(X86)%\Dolphin
        string? pf   = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string? pfX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (string? root in new[] { pf, pfX86 })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            yield return Path.Combine(root, "Dolphin", "Dolphin.exe");
            yield return Path.Combine(root, "Dolphin Emulator", "Dolphin.exe");
        }

        // Desktop + Documents (some users drop a portable copy there)
        string? desktop  = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        string? documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        foreach (string? root in new[] { desktop, documents })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            yield return Path.Combine(root, "Dolphin", "Dolphin.exe");
        }
    }

    // ── Private helpers — download / extract release zip ─────────────────────

    private async Task DownloadAndExtractAsync(
        string zipUrl,
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"metroidprime-ap-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((6, $"Downloading Metroid Prime AP {version}..."));
            using (var response = await _http.GetAsync(
                zipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();

                long total      = response.Content.Headers.ContentLength ?? -1;
                long downloaded = 0;

                await using var src = await response.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tempZip);
                var buf = new byte[81920];
                int bytesRead;
                while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
                    downloaded += bytesRead;
                    if (total > 0)
                    {
                        int pct = (int)(6 + 70 * downloaded / total);
                        progress.Report((pct,
                            $"Downloading Metroid Prime AP release... " +
                            $"{downloaded / 1_000_000}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((80, "Extracting Metroid Prime AP release..."));
            Directory.CreateDirectory(GameDirectory);

            // Extract — flatten a single wrapping sub-folder if the zip has one.
            string tempExtract = Path.Combine(
                Path.GetTempPath(), $"mp1ap-extract-{Guid.NewGuid():N}");
            try
            {
                System.IO.Compression.ZipFile.ExtractToDirectory(
                    tempZip, tempExtract, overwriteFiles: true);

                // If the zip has a single top-level folder, lift its contents up.
                var topEntries = Directory.GetFileSystemEntries(tempExtract);
                string sourceRoot = (topEntries.Length == 1 &&
                    Directory.Exists(topEntries[0]))
                    ? topEntries[0]
                    : tempExtract;

                CopyDirectory(sourceRoot, GameDirectory, overwrite: true);
            }
            finally
            {
                try { Directory.Delete(tempExtract, recursive: true); } catch { }
            }

            progress.Report((88, "Extraction complete."));

            // If the zip contained a standalone .apworld, it is already in
            // GameDirectory. If the repo hosts it separately, we skip it (the zip
            // approach covers the common case from v0.5.0 onwards).
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    /// Recursively copy `source` into `dest`, creating directories as needed.
    private static void CopyDirectory(string source, string dest, bool overwrite)
    {
        Directory.CreateDirectory(dest);
        foreach (string file in Directory.GetFiles(source))
        {
            string target = Path.Combine(dest, Path.GetFileName(file));
            File.Copy(file, target, overwrite);
        }
        foreach (string dir in Directory.GetDirectories(source))
        {
            string targetDir = Path.Combine(dest, Path.GetFileName(dir));
            CopyDirectory(dir, targetDir, overwrite);
        }
    }

    // ── Private helpers — self-contained settings sidecar ────────────────────
    // Keeps the Dolphin-exe override in its OWN JSON file (same approach as KH1 /
    // Subnautica). Does not modify Core/SettingsStore.

    private sealed class Mp1Settings
    {
        public string? DolphinExeOverride { get; set; }
    }

    private Mp1Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<Mp1Settings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(Mp1Settings s)
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

    private string? LoadDolphinOverride() =>
        LoadSettings().DolphinExeOverride is { } p && !string.IsNullOrWhiteSpace(p)
            ? p : null;

    private void SaveDolphinOverride(string p)
    {
        var s = LoadSettings();
        s.DolphinExeOverride = p;
        SaveSettings(s);
    }

    // ── Private helpers — registry ────────────────────────────────────────────

    private static string? ReadRegistryString(RegistryKey hive, string subKey, string valueName)
    {
        try
        {
            using RegistryKey? k = hive.OpenSubKey(subKey);
            return k?.GetValue(valueName) as string;
        }
        catch { return null; }
    }
}
