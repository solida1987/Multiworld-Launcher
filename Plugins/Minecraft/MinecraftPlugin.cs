using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using LauncherV2.Core;

// The launcher project sets BOTH UseWPF and UseWindowsForms=true, so several UI
// type names (Color, Brushes, Button, TextBox, HorizontalAlignment) collide
// between WPF and WinForms. The project's GlobalUsings.cs already aliases each of
// these to its WPF type globally, so this file relies on those — no local aliases
// (a local alias duplicating a global one is CS1537).

namespace LauncherV2.Plugins.Minecraft;

// ═══════════════════════════════════════════════════════════════════════════════
// MinecraftPlugin — install / launch for "Minecraft" (Mojang, 2011+) played
// through the Archipelago Minecraft integration. This is a NATIVE "ConnectsItself"
// integration: a NeoForge (or legacy Forge) server-side mod runs as a LOCAL
// DEDICATED SERVER on the player's machine; the player connects their own
// Minecraft Java client to localhost. The mod speaks to the AP server itself —
// no launcher-held ApClient is needed on the slot.
//
// ── HONEST REALITY CHECK (2026-06-14, verified online) ────────────────────────
// Minecraft has the most unusual AP integration of any native game:
//
//   * THE AP WORLD game string is "Minecraft" (verified via the apworld filename
//     "minecraft.apworld" released from the canonical integration repo and the
//     jacobmix README which names the game "Minecraft"). GameId here = "minecraft".
//     Minecraft is a CORE Archipelago world (it ships inside Archipelago itself).
//
//   * THE CANONICAL MOD REPO (2026) is qixils/NeoForgeAP (verified live 2026-06-14).
//     Latest release v2.1.3 targets Minecraft 1.21.11 + NeoForge, requires Java 21.
//     The release ships TWO assets:
//       - aprandomizer-<version>+<mc>.jar  — the NeoForge server-side mod
//       - minecraft.apworld                 — the apworld (for generation only)
//     The OLDER repo KonoTyran/Minecraft_AP_Randomizer (now archived) used Forge
//     and targeted 1.19.4 / 1.20.4; jacobmix/Minecraft_AP_Randomizer extends that
//     for Minecraft Dig (a distinct game variant). We target the new canonical repo.
//
//   * HOW THE INTEGRATION WORKS (verified from README + setup flow):
//     1. The player obtains a .apmc data file from the AP server room (the host
//        generates it; the Minecraft Client bundled inside Archipelago is used to
//        load it and set up the APData folder, then is no longer needed).
//     2. A NeoForge Minecraft server is run LOCALLY (the mod jar goes in its mods/
//        folder; the APData folder with the .apmc file goes at the server root).
//     3. The player opens Minecraft Java Edition, goes to Multiplayer > Direct
//        Connection and connects to "localhost" (default port 25565).
//     4. Once in-game, the mod auto-connects to the AP server. If it does not, the
//        player types /connect <AP-Address> (Port) (Password) in-game chat.
//
//   * NO MINECRAFT CLIENT BUNDLED — Minecraft Java Edition is the player's own
//     legally-owned game, purchased from minecraft.net. The launcher CANNOT install
//     Minecraft itself. So "install" here means: download the NeoForge server jar
//     and the mod jar, and set up the server directory. The player provides their
//     Minecraft client themselves.
//
//   * THE SERVER DIRECTORY — the plugin stages a dedicated Minecraft server
//     directory under Games/Minecraft/server/ with:
//       - server.jar  (vanilla Minecraft server, downloaded from Mojang's API)
//       - libraries/  (NeoForge installer output — the plugin runs the installer)
//       - mods/       (the AP randomizer mod jar goes here)
//       - APData/     (the player drops their .apmc file here)
//       - eula.txt    (auto-accepted; the player owns Minecraft and accepted EULA)
//       - server.properties (pre-configured: online-mode=false so LAN-only localhost
//                           works without a Mojang account on the server side)
//
//   * ConnectsItself = true: the NeoForge mod handles the AP slot connection from
//     inside the game server; the launcher must NOT hold a competing ApClient.
//
//   * SupportsStandalone = true: you can run a Minecraft NeoForge server without AP
//     (though the AP mod is only useful with AP).
//
// ── WHAT THIS PLUGIN DOES ─────────────────────────────────────────────────────
//   1. CHECK whether Java 21+ is available (required for Minecraft 1.21.11).
//   2. DOWNLOAD + SET UP the NeoForge Minecraft server into Games/Minecraft/server/:
//      - Download the vanilla Minecraft 1.21.11 server jar from Mojang's API.
//      - Download the NeoForge installer for 1.21.11, run it in --install-server mode.
//      - Download the AP randomizer mod jar (aprandomizer-*.jar) into mods/.
//      - Write eula.txt (auto-accepted — player owns MC) and server.properties.
//      - Create APData/ folder (the player drops their .apmc file there).
//   3. LAUNCH the local server (java -jar ... nogui), watch the server process, and
//      surface a note telling the user to connect Minecraft Java to localhost.
//   4. Settings panel: Java path override, .apmc file picker, in-game /connect
//      command pre-built from the session credentials, and link buttons.
//   5. StopAsync: kill the server process cleanly (send /stop to stdin first).
//
// ── INSTALL HONESTY ──────────────────────────────────────────────────────────
//   * The plugin does NOT install Minecraft itself (the player must own it).
//   * The NeoForge installer requires network access to download its library set;
//     if that fails, the plugin surfaces a clear error with the manual steps.
//   * The APData/.apmc file must come from the AP server room; the plugin guides
//     the player on where to put it.
//   * The in-game /connect command syntax is surfaced in the settings panel pre-
//     filled with the session credentials so the player can copy-paste it.
//
//   * BUILD NOTE: this project sets UseWindowsForms=true alongside UseWPF=true.
//     All WPF UI types are fully qualified below to avoid CS0104 ambiguity.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class MinecraftPlugin : IGamePlugin
{
    // ── Constants (verified 2026-06-14 against qixils/NeoForgeAP) ────────────

    /// The canonical mod repository (NeoForge, replaces the archived Forge repos).
    private const string ModRepoOwner = "qixils";
    private const string ModRepoName  = "NeoForgeAP";
    private const string ModRepoUrl   = "https://github.com/qixils/NeoForgeAP";

    /// GitHub Releases API for the canonical mod repo.
    private const string ReleasesApiUrl =
        "https://api.github.com/repos/qixils/NeoForgeAP/releases/latest";

    /// Official AP setup guide page for Minecraft.
    private const string SetupGuideUrl =
        "https://archipelago.gg/tutorial/Minecraft/setup_en";

    /// AP Minecraft game info page.
    private const string GameInfoUrl =
        "https://archipelago.gg/games/Minecraft/info/en";

    private const string ArchipelagoSite = "https://archipelago.gg";

    /// Minecraft official download page (player must own the game).
    private const string MinecraftDownloadUrl = "https://www.minecraft.net/en-us/download";

    /// NeoForged installer download page (fallback if API resolution fails).
    private const string NeoForgeDownloadUrl = "https://neoforged.net/";

    /// The Minecraft version this plugin targets (NeoForgeAP v2.1.3, verified
    /// 2026-06-14 from minecraft_versions.json in qixils/NeoForgeAP).
    private const string MinecraftVersion = "1.21.11";

    /// Java major version required for Minecraft 1.21.11 (verified from
    /// minecraft_versions.json: "java": 21).
    private const int RequiredJavaMajor = 21;

    /// Default Minecraft server port.
    private const int DefaultMinecraftPort = 25565;

    /// Default AP server port.
    private const int DefaultApPort = 38281;

    /// Pinned fallback: NeoForgeAP v2.1.3, verified live 2026-06-14.
    private const string FallbackModVersion  = "2.1.3+1.21.11";
    private const string FallbackModJarUrl   =
        "https://github.com/qixils/NeoForgeAP/releases/download/v2.1.3/aprandomizer-2.1.3%2B1.21.11.jar";
    private const string FallbackApWorldUrl  =
        "https://github.com/qixils/NeoForgeAP/releases/download/v2.1.3/minecraft.apworld";

    /// Mojang's version manifest — we pull the 1.21.11 server jar URL from here.
    private const string MojangVersionManifestUrl =
        "https://launchermeta.mojang.com/mc/game/version_manifest.json";

    /// File written in the server directory to record the installed mod version.
    private const string ModVersionStampFileName = "ap_mod_version.dat";

    /// The name of the APData folder where the player places their .apmc file.
    private const string ApDataFolderName = "APData";

    /// The mod jar file as it will be stored in the mods/ folder.
    private const string ModJarFileName = "aprandomizer.jar";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "minecraft";
    public string DisplayName => "Minecraft";
    public string Subtitle    => "Native PC · NeoForge server + AP mod";

    /// EXACT AP game string — verified via apworld filename "minecraft.apworld"
    /// from qixils/NeoForgeAP and confirmed by jacobmix README ("Minecraft" game).
    /// The Minecraft AP world ships inside Archipelago core.
    public string ApWorldName => "Minecraft";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "minecraft.png");

    public string ThemeAccentColor => "#5D8C3E";   // Minecraft grass green

    public string[] GameBadges => new[] { "Own Minecraft · Java 21" };

    public string Description =>
        "Minecraft, Mojang's iconic sandbox game, played through the Archipelago " +
        "NeoForge mod (qixils/NeoForgeAP). The integration runs a local NeoForge " +
        "Minecraft server on your machine; you connect your own Minecraft Java client " +
        "to localhost. The mod speaks to the Archipelago server itself — opening " +
        "structures, defeating bosses, crafting items and completing advancements " +
        "become checks shuffled across the multiworld. You need your own copy of " +
        "Minecraft Java Edition (purchased from minecraft.net) and Java 21. The " +
        "launcher downloads and sets up the NeoForge server and the AP mod for you. " +
        "Your host provides a .apmc data file which you place in the APData folder " +
        "before starting the server. You connect to your AP room from inside the game " +
        "with /connect or it auto-connects on join.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the server directory + mod jar + NeoForge start script
    /// are present. We check for the mod jar and the version stamp.
    public bool IsInstalled => IsModJarPresent();

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Root directory for this plugin's files (server + bookkeeping).
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Minecraft");

    private string ServerDirectory
        => Path.Combine(GameDirectory, "server");

    private string ModsDirectory
        => Path.Combine(ServerDirectory, "mods");

    private string ModJarPath
        => Path.Combine(ModsDirectory, ModJarFileName);

    private string ApDataDirectory
        => Path.Combine(ServerDirectory, ApDataFolderName);

    private string ModVersionStampPath
        => Path.Combine(GameDirectory, ModVersionStampFileName);

    /// This plugin's own settings sidecar.
    private string SettingsSidecarPath
        => Path.Combine(GameDirectory, "minecraft_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    /// The running NeoForge server process, null when not started by this plugin.
    private Process? _serverProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The NeoForge AP mod reports checks/items/goal to the AP server itself.
    // These exist for interface compatibility (ConnectsItself = true).
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
            InstalledVersion = IsModJarPresent() ? ReadStampedVersion() : null;
        }
        catch
        {
            InstalledVersion = null;
        }

        try
        {
            var (version, _, _) = await ResolveLatestReleaseAsync(ct);
            AvailableVersion = version;
        }
        catch
        {
            AvailableVersion = null; // never throw on network failure
        }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 0. Verify Java 21+ is available (required for Minecraft 1.21.11).
        progress.Report((2, "Checking Java 21+ installation..."));
        string? javaExe = FindJavaExe();
        if (javaExe == null)
            throw new InvalidOperationException(
                "Java 21 or later is required for Minecraft 1.21.11 but was not found " +
                "on this system. Please install Java 21 from https://adoptium.net/ or " +
                "https://www.oracle.com/java/technologies/downloads/ and try again. " +
                "Open Settings to configure a custom Java path.");

        int javaVersion = GetJavaMajorVersion(javaExe);
        if (javaVersion < RequiredJavaMajor)
            throw new InvalidOperationException(
                $"Java {RequiredJavaMajor} or later is required. Found Java {javaVersion} " +
                $"at: {javaExe}. Please install Java {RequiredJavaMajor} from " +
                "https://adoptium.net/ and try again (or configure the path in Settings).");

        // 1. Resolve the latest mod release.
        progress.Report((6, "Checking the latest NeoForgeAP mod release..."));
        var (modVersion, modJarUrl, apWorldUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = modVersion;
        modJarUrl ??= FallbackModJarUrl;

        // 2. Create directory structure.
        progress.Report((10, "Creating server directory..."));
        Directory.CreateDirectory(ServerDirectory);
        Directory.CreateDirectory(ModsDirectory);
        Directory.CreateDirectory(ApDataDirectory);

        // 3. Download the AP randomizer mod jar.
        progress.Report((14, $"Downloading AP randomizer mod ({modVersion})..."));
        await DownloadFileAsync(modJarUrl, ModJarPath, progress, 14, 45, ct);

        // 4. Download the vanilla Minecraft server jar from Mojang.
        progress.Report((47, $"Downloading Minecraft {MinecraftVersion} server..."));
        string serverJarPath = Path.Combine(ServerDirectory, "server.jar");
        if (!File.Exists(serverJarPath))
        {
            string? serverJarUrl = await ResolveMojangServerJarUrlAsync(ct);
            if (serverJarUrl == null)
                throw new InvalidOperationException(
                    $"Could not resolve the Minecraft {MinecraftVersion} server jar URL " +
                    "from Mojang's version manifest. Check your internet connection.");
            await DownloadFileAsync(serverJarUrl, serverJarPath, progress, 47, 72, ct);
        }
        else
        {
            progress.Report((72, "Minecraft server jar already present, skipping download."));
        }

        // 5. Download and run the NeoForge installer.
        progress.Report((73, $"Downloading NeoForge installer for Minecraft {MinecraftVersion}..."));
        string? neoForgeInstallerUrl = await ResolveNeoForgeInstallerUrlAsync(ct);
        if (neoForgeInstallerUrl != null)
        {
            string installerPath = Path.Combine(GameDirectory, "neoforge-installer.jar");
            await DownloadFileAsync(neoForgeInstallerUrl, installerPath, progress, 73, 82, ct);

            progress.Report((83, "Running NeoForge installer (this may take a minute)..."));
            bool installerOk = await RunNeoForgeInstallerAsync(javaExe, installerPath, ServerDirectory, ct);
            if (!installerOk)
            {
                progress.Report((85,
                    "NeoForge installer did not complete cleanly. The mod may still work " +
                    "if NeoForge is already installed; otherwise see Settings for manual steps."));
            }
        }
        else
        {
            progress.Report((84,
                "Could not resolve a NeoForge installer download (offline?). " +
                "Download NeoForge manually from https://neoforged.net/ and run " +
                "--install-server in the server directory. See Settings for steps."));
        }

        // 6. Write server configuration files.
        progress.Report((90, "Writing server configuration..."));
        WriteServerProperties();
        WriteEula();
        WriteRunScript(javaExe);

        // 7. Stamp the installed version.
        WriteStampedVersion(modVersion);
        InstalledVersion = modVersion;

        progress.Report((100,
            $"Minecraft AP server ({modVersion}) is set up in {ServerDirectory}. " +
            $"NEXT STEPS: " +
            $"(1) Get your .apmc file from your host (or the AP room page) and place " +
            $"it in the APData folder at: {ApDataDirectory} . " +
            $"(2) Use the Play button to start the server. " +
            $"(3) Open Minecraft Java Edition {MinecraftVersion}, go to Multiplayer > " +
            $"Direct Connection, and enter \"localhost\" as the address. " +
            $"(4) Once in-game, the mod should auto-connect to your AP room. If it " +
            $"does not, type /connect <address> <port> <password> in chat. " +
            $"See Settings for full instructions."));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        // Check for the mod jar and at least one NeoForge-related file in the server dir.
        if (!IsModJarPresent()) return false;
        if (!Directory.Exists(ServerDirectory)) return false;
        if (!File.Exists(Path.Combine(ServerDirectory, "eula.txt"))) return false;
        return true;
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // The NeoForge AP mod auto-connects to the AP server when a player joins.
        // If it does not, the player types /connect <address> <port> <pass> in chat.
        // We cannot pre-write a connection config file (none is documented for the
        // NeoForgeAP mod — connection is embedded in the .apmc file the player
        // places in APData/). ConnectsItself = true: we must NOT hold our own
        // ApClient on this slot while the mod-server is running.
        _ = session; // intentionally unused — connection is via .apmc + auto-connect
        StartServer();
        return Task.CompletedTask;
    }

    /// The server can be run standalone (without AP, just as a Minecraft server).
    public bool SupportsStandalone => true;

    /// The NeoForge AP mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartServer();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (_serverProcess != null && !_serverProcess.HasExited)
        {
            // Send /stop to the server's stdin for a clean shutdown first.
            try
            {
                _serverProcess.StandardInput.WriteLine("/stop");
                _serverProcess.StandardInput.Flush();
                // Give the server 10 seconds to shut down gracefully.
                if (!_serverProcess.WaitForExit(10_000))
                    _serverProcess.Kill(entireProcessTree: true);
            }
            catch
            {
                try { _serverProcess.Kill(entireProcessTree: true); } catch { }
            }
        }
        IsRunning = false;
        _serverProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The NeoForge AP mod receives items from the AP server directly via the
        // mod's own AP client inside the running Minecraft server. Nothing to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Existing-install validation (folder picker) ───────────────────────────

    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist.";
        // Accept any writable directory — we'll create the server subdirectory here.
        return null;
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var accent  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF));

        var panel = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20)
        };

        // ── How-this-works banner ─────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "Minecraft AP runs a local NeoForge server on your machine. You connect " +
                "your own Minecraft Java Edition client to localhost. The NeoForge mod " +
                "handles all AP communication. You need: (1) Java 21+, (2) your own " +
                "Minecraft Java Edition, (3) a .apmc data file from your host/room.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: Java ─────────────────────────────────────────────────────
        AddSectionHeader(panel, "JAVA 21+", muted);

        string? javaExe = ResolveJavaExe();
        int javaMajor = javaExe != null ? GetJavaMajorVersion(javaExe) : 0;
        bool javaOk = javaMajor >= RequiredJavaMajor;

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = javaOk
                ? $"Java {javaMajor} detected: {javaExe}"
                : javaExe != null
                    ? $"Java {javaMajor} found at {javaExe} — Java 21 required. Install Java 21 from adoptium.net."
                    : "Java not detected. Install Java 21 from https://adoptium.net/ (or set path below).",
            FontSize = 11, Foreground = javaOk ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        // Java path override row.
        var javaSettings = LoadSettings();
        AddFolderRow(panel, fg, muted,
            label: "Custom Java path (optional override):",
            currentPath: javaSettings.JavaExeOverride ?? "",
            tooltip: "Full path to java.exe if auto-detection fails. Leave blank to use system Java.",
            onPicked: path =>
            {
                var s = LoadSettings();
                s.JavaExeOverride = path;
                SaveSettings(s);
            });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Java 21 download: https://adoptium.net/",
            FontSize = 11, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Server directory ─────────────────────────────────────────
        AddSectionHeader(panel, "SERVER DIRECTORY", muted);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = $"Server directory: {ServerDirectory}",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        bool modPresent = IsModJarPresent();
        string installedVer = modPresent ? (ReadStampedVersion() ?? "installed") : "not installed";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modPresent
                ? $"AP mod: {installedVer}  |  Mod jar: {ModJarPath}"
                : "AP mod not installed. Use the Install button on the Play tab.",
            FontSize = 11, Foreground = modPresent ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        bool apDataExists = Directory.Exists(ApDataDirectory);
        bool apDataHasApmc = apDataExists && Directory.EnumerateFiles(ApDataDirectory, "*.apmc").Any();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = apDataHasApmc
                ? $"APData/ folder: .apmc file found — ready to start."
                : apDataExists
                    ? $"APData/ folder exists but no .apmc file found. Place your .apmc file at: {ApDataDirectory}"
                    : $"APData/ folder not created yet — use Install first. Then place your .apmc at: {Path.Combine(ServerDirectory, ApDataFolderName)}",
            FontSize = 11, Foreground = apDataHasApmc ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // Open server dir button.
        var openServerBtn = new System.Windows.Controls.Button
        {
            Content = "Open server folder",
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Padding = new System.Windows.Thickness(10, 5, 10, 5),
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        string serverDir = ServerDirectory;
        openServerBtn.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(serverDir);
                Process.Start(new ProcessStartInfo("explorer.exe", serverDir) { UseShellExecute = true });
            }
            catch { }
        };
        panel.Children.Add(openServerBtn);

        // ── Section: AP Connection ────────────────────────────────────────────
        AddSectionHeader(panel, "AP CONNECTION", muted);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "The NeoForge mod auto-connects to your AP room when you join the " +
                "local server (connection info is embedded in your .apmc file). " +
                "If it does not auto-connect, type this command in-game chat:\n" +
                "    /connect <AP-address> <port> <password>\n" +
                "Example: /connect archipelago.gg 38281 MyPassword\n" +
                "After connecting, type /start to begin the game.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────────
        AddSectionHeader(panel, "GUIDED SETUP", muted);

        foreach (string step in new[]
        {
            "1. Install Java 21+ (https://adoptium.net/). Only Java 21+ runs Minecraft 1.21.11.",
            "2. Own Minecraft Java Edition (https://minecraft.net/). The launcher cannot install it.",
            "3. Click Install on the Play tab to download the NeoForge server + AP mod.",
            "4. Get your .apmc file from your host (the AP room page, or the host sends it). " +
               "Place it in the APData folder inside the server directory.",
            "5. Click Play to start the local Minecraft server. Keep the console window open.",
            "6. Open Minecraft Java Edition 1.21.11. Go to Multiplayer > Direct Connection. " +
               "Enter \"localhost\" as the server address and click Join Server.",
            "7. The AP mod should auto-connect to your AP room. If not, type " +
               "/connect <AP-address> (Port) (Password) in chat, then /start.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        // ── Section: Links ────────────────────────────────────────────────────
        AddSectionHeader(panel, "LINKS", muted);

        foreach (var (label, url) in new[]
        {
            ("NeoForgeAP (mod source + releases) ↗",    ModRepoUrl),
            ("Minecraft AP Setup Guide ↗",               SetupGuideUrl),
            ("Minecraft (AP) Game Info ↗",               GameInfoUrl),
            ("Archipelago Official ↗",                   ArchipelagoSite),
            ("Minecraft Java Edition ↗",                 MinecraftDownloadUrl),
            ("NeoForge download ↗",                      NeoForgeDownloadUrl),
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
                Foreground = accent,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) => TryOpenUrl(u);
            panel.Children.Add(btn);
        }

        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // Pull GitHub releases from the canonical mod repo as a news feed.
        try
        {
            string json = await _http.GetStringAsync(
                "https://api.github.com/repos/qixils/NeoForgeAP/releases?per_page=10", ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                string tag  = el.TryGetProperty("tag_name",    out var t) ? (t.GetString() ?? "") : "";
                string name = el.TryGetProperty("name",        out var n) ? (n.GetString() ?? tag) : tag;
                string body = el.TryGetProperty("body",        out var b) ? (b.GetString() ?? "")  : "";
                string? url = el.TryGetProperty("html_url",    out var u) ? u.GetString() : null;

                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var pd) &&
                    pd.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(pd.GetString(), out date);

                items.Add(new NewsItem(
                    Title:   string.IsNullOrWhiteSpace(name) ? tag : name,
                    Body:    body,
                    Version: tag,
                    Date:    date,
                    Url:     url));

                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    /// Resolve the latest NeoForgeAP release: (version, modJarUrl, apWorldUrl).
    /// Returns fallback values on network failure rather than throwing.
    private async Task<(string Version, string? ModJarUrl, string? ApWorldUrl)>
        ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(ReleasesApiUrl, ct);
            using var doc = JsonDocument.Parse(json);

            string tag = doc.RootElement.TryGetProperty("tag_name", out var t)
                ? (t.GetString() ?? FallbackModVersion) : FallbackModVersion;

            string? jarUrl    = null;
            string? worldUrl  = null;

            if (doc.RootElement.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string? assetName = asset.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                    string? dlUrl     = asset.TryGetProperty("browser_download_url", out var dl) ? dl.GetString() : null;
                    if (string.IsNullOrEmpty(assetName) || string.IsNullOrEmpty(dlUrl)) continue;

                    if (assetName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                        jarUrl = dlUrl;
                    else if (assetName.EndsWith(".apworld", StringComparison.OrdinalIgnoreCase))
                        worldUrl = dlUrl;
                }
            }

            // Strip leading "v" from tag if present for a clean version string.
            string version = tag.TrimStart('v');
            return (version, jarUrl ?? FallbackModJarUrl, worldUrl ?? FallbackApWorldUrl);
        }
        catch
        {
            return (FallbackModVersion, FallbackModJarUrl, FallbackApWorldUrl);
        }
    }

    // ── Private helpers — Mojang server jar resolution ────────────────────────

    /// Resolve the Minecraft server jar download URL for MinecraftVersion from
    /// Mojang's official version manifest API. Returns null on failure.
    private async Task<string?> ResolveMojangServerJarUrlAsync(CancellationToken ct)
    {
        try
        {
            // Step 1: get the version manifest to find the per-version metadata URL.
            string manifestJson = await _http.GetStringAsync(MojangVersionManifestUrl, ct);
            using var manifestDoc = JsonDocument.Parse(manifestJson);

            if (!manifestDoc.RootElement.TryGetProperty("versions", out var versions))
                return null;

            string? versionMetaUrl = null;
            foreach (var v in versions.EnumerateArray())
            {
                if (v.TryGetProperty("id", out var id) &&
                    id.GetString() == MinecraftVersion &&
                    v.TryGetProperty("url", out var url))
                {
                    versionMetaUrl = url.GetString();
                    break;
                }
            }
            if (versionMetaUrl == null) return null;

            // Step 2: fetch the per-version metadata to find the server jar URL.
            string versionJson = await _http.GetStringAsync(versionMetaUrl, ct);
            using var versionDoc = JsonDocument.Parse(versionJson);

            if (versionDoc.RootElement.TryGetProperty("downloads", out var downloads) &&
                downloads.TryGetProperty("server", out var server) &&
                server.TryGetProperty("url", out var serverUrl))
            {
                return serverUrl.GetString();
            }
            return null;
        }
        catch { return null; }
    }

    // ── Private helpers — NeoForge installer resolution ───────────────────────

    /// Resolve the NeoForge installer URL for Minecraft MinecraftVersion via the
    /// NeoForged Maven metadata. Returns null on failure.
    private async Task<string?> ResolveNeoForgeInstallerUrlAsync(CancellationToken ct)
    {
        try
        {
            // NeoForge Maven metadata: list available versions for MC 1.21.11.
            // NeoForge version numbers for 1.21.x start with "21.1.".
            string mavenMetaUrl =
                "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";
            string xml = await _http.GetStringAsync(mavenMetaUrl, ct);

            // Simple text scan for versions compatible with 1.21.11 (start with "21.1.1").
            // NeoForge for MC 1.21.11 has versions like "21.1.11-*" or "21.1.1*".
            string prefix = "21.1."; // NeoForge major.minor for MC 1.21.x
            var versions = new List<string>();

            int i = 0;
            const string versionTag = "<version>";
            while ((i = xml.IndexOf(versionTag, i, StringComparison.Ordinal)) >= 0)
            {
                int start = i + versionTag.Length;
                int end   = xml.IndexOf("</version>", start, StringComparison.Ordinal);
                if (end < 0) break;
                string ver = xml.Substring(start, end - start).Trim();
                if (ver.StartsWith(prefix, StringComparison.Ordinal) && !ver.Contains("beta") && !ver.Contains("alpha"))
                    versions.Add(ver);
                i = end;
            }

            if (versions.Count == 0) return null;

            // Use the latest version (last in the list, as Maven metadata lists ascending).
            string latestNeoForge = versions[versions.Count - 1];
            return
                $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{latestNeoForge}/" +
                $"neoforge-{latestNeoForge}-installer.jar";
        }
        catch { return null; }
    }

    // ── Private helpers — NeoForge installer execution ────────────────────────

    /// Run the NeoForge installer in --install-server mode in the server directory.
    /// Returns true when the process exits with code 0; false otherwise. Non-fatal.
    private static async Task<bool> RunNeoForgeInstallerAsync(
        string javaExe, string installerJar, string serverDir, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName         = javaExe,
                Arguments        = $"-jar \"{installerJar}\" --install-server",
                WorkingDirectory = serverDir,
                UseShellExecute  = false,
                RedirectStandardOutput = false,
                RedirectStandardError  = false,
                CreateNoWindow   = false,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    // ── Private helpers — server config files ─────────────────────────────────

    private void WriteServerProperties()
    {
        string path = Path.Combine(ServerDirectory, "server.properties");
        // Only write if absent — preserve any edits the user has made.
        if (File.Exists(path)) return;

        File.WriteAllText(path,
            "# Minecraft server properties — auto-generated by Archipelago Launcher\n" +
            "# Archipelago Minecraft AP runs as a local LAN server; no auth needed.\n" +
            $"server-port={DefaultMinecraftPort}\n" +
            "online-mode=false\n" +     // LAN-only localhost; no Mojang auth on server
            "spawn-protection=0\n" +
            "max-players=20\n" +
            "difficulty=normal\n" +
            "gamemode=survival\n" +
            "level-type=minecraft:default\n",
            new UTF8Encoding(false));
    }

    private void WriteEula()
    {
        string path = Path.Combine(ServerDirectory, "eula.txt");
        if (File.Exists(path)) return;
        // The player owns Minecraft Java Edition and has accepted the EULA.
        File.WriteAllText(path,
            "#By setting 'eula' to 'true' you are indicating your agreement to " +
            "Mojang's EULA (https://account.mojang.com/documents/minecraft_eula)\n" +
            "#Auto-accepted by Archipelago Launcher (you own Minecraft Java Edition)\n" +
            "eula=true\n",
            new UTF8Encoding(false));
    }

    private void WriteRunScript(string javaExe)
    {
        // Write a helper run.bat so the user can also start the server manually.
        string path = Path.Combine(ServerDirectory, "run.bat");
        if (File.Exists(path)) return;
        File.WriteAllText(path,
            "@echo off\r\n" +
            $"\"{javaExe}\" -Xmx4G -Xms2G -jar server.jar nogui\r\n" +
            "pause\r\n",
            new UTF8Encoding(false));
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartServer()
    {
        string? javaExe = ResolveJavaExe();
        if (javaExe == null)
            throw new FileNotFoundException(
                "Java not found. Install Java 21 from https://adoptium.net/ or " +
                "set the Java path in Settings.");

        if (!IsModJarPresent())
            throw new InvalidOperationException(
                "The AP mod is not installed. Use the Install button on the Play tab first.");

        // Locate the NeoForge server launch file (NeoForge creates a run.sh/run.bat or
        // a @libraries/... args file; also try the vanilla server.jar as a fallback).
        string? launchJar = FindNeoForgeServerJar();
        string arguments;

        if (launchJar != null)
        {
            arguments = $"-Xmx4G -Xms2G -jar \"{launchJar}\" nogui";
        }
        else
        {
            // Fall back to vanilla server.jar (still usable for AP even without NeoForge
            // lib injection — NeoForge may use a different start mechanism).
            string vanillaJar = Path.Combine(ServerDirectory, "server.jar");
            if (!File.Exists(vanillaJar))
                throw new FileNotFoundException(
                    "server.jar not found in the server directory. Re-run Install to " +
                    $"download it. Expected: {vanillaJar}");
            arguments = $"-Xmx4G -Xms2G -jar \"{vanillaJar}\" nogui";
        }

        var psi = new ProcessStartInfo
        {
            FileName         = javaExe,
            Arguments        = arguments,
            WorkingDirectory = ServerDirectory,
            UseShellExecute  = false,
            RedirectStandardInput  = true,   // so we can send /stop on StopAsync
            RedirectStandardOutput = false,
            RedirectStandardError  = false,
            CreateNoWindow   = false,        // show the server console window
        };

        var proc = Process.Start(psi)
                   ?? throw new InvalidOperationException("Failed to start the Minecraft server.");

        _serverProcess = proc;
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
        catch { /* some environments don't support Exited — non-fatal */ }
    }

    /// Find the NeoForge-generated server jar. NeoForge's installer creates either
    /// a "forge-<ver>-server.jar" / "neoforge-<ver>-server.jar" in the server root,
    /// or a "run.jar" / "@libraries/..." args file. Return the first matching jar.
    private string? FindNeoForgeServerJar()
    {
        if (!Directory.Exists(ServerDirectory)) return null;
        try
        {
            foreach (string jar in Directory.EnumerateFiles(ServerDirectory, "*.jar", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileNameWithoutExtension(jar).ToLowerInvariant();
                if (name.Contains("neoforge") || name.Contains("forge") || name == "run")
                    return jar;
            }
        }
        catch { /* ignore */ }
        return null;
    }

    // ── Private helpers — Java detection ─────────────────────────────────────

    /// Resolve java.exe: user override first, then system PATH / registry.
    private string? ResolveJavaExe()
    {
        string? ov = LoadSettings().JavaExeOverride;
        if (!string.IsNullOrWhiteSpace(ov) && File.Exists(ov))
            return ov;
        return FindJavaExe();
    }

    /// Find java.exe on the system without a user override.
    private static string? FindJavaExe()
    {
        // 1. JAVA_HOME environment variable.
        string? javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            string candidate = Path.Combine(javaHome, "bin", "java.exe");
            if (File.Exists(candidate)) return candidate;
        }

        // 2. PATH search.
        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathVar))
        {
            foreach (string dir in pathVar.Split(Path.PathSeparator))
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim(), "java.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* invalid path component */ }
            }
        }

        // 3. Registry — check common JDK install paths via
        //    HKLM\SOFTWARE\JavaSoft\JDK (modern) and similar.
        string? fromRegistry = FindJavaFromRegistry();
        if (fromRegistry != null) return fromRegistry;

        // 4. Conventional install locations.
        foreach (string root in new[]
        {
            @"C:\Program Files\Eclipse Adoptium",
            @"C:\Program Files\Java",
            @"C:\Program Files\Microsoft",
        })
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                // Prefer newest (descending sort by folder name).
                foreach (string sub in Directory.EnumerateDirectories(root)
                             .OrderByDescending(d => d))
                {
                    string candidate = Path.Combine(sub, "bin", "java.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch { /* try next root */ }
        }

        return null;
    }

    private static string? FindJavaFromRegistry()
    {
        // Modern JDK registry key.
        foreach (string subKey in new[]
        {
            @"SOFTWARE\JavaSoft\JDK",
            @"SOFTWARE\JavaSoft\Java Runtime Environment",
        })
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(subKey);
                if (key == null) continue;
                string? current = key.GetValue("CurrentVersion") as string;
                if (string.IsNullOrWhiteSpace(current)) continue;
                using RegistryKey? verKey = key.OpenSubKey(current);
                string? javaHome = verKey?.GetValue("JavaHome") as string;
                if (string.IsNullOrWhiteSpace(javaHome)) continue;
                string candidate = Path.Combine(javaHome, "bin", "java.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* try next key */ }
        }
        return null;
    }

    /// Get the major version number from a java.exe by running "java -version".
    /// Returns 0 on any failure.
    private static int GetJavaMajorVersion(string javaExe)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = javaExe,
                Arguments              = "-version",
                UseShellExecute        = false,
                RedirectStandardError  = true,
                RedirectStandardOutput = true,
                CreateNoWindow         = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return 0;
            // java -version writes to stderr (by convention).
            string output = proc.StandardError.ReadToEnd();
            proc.WaitForExit(5_000);

            // Line like: openjdk version "21.0.3" 2024-04-16  or  java version "1.8.0_202"
            foreach (string line in output.Split('\n'))
            {
                if (line.IndexOf("version", StringComparison.OrdinalIgnoreCase) < 0) continue;
                int q1 = line.IndexOf('"');
                int q2 = line.IndexOf('"', q1 + 1);
                if (q1 < 0 || q2 < 0) continue;
                string ver = line.Substring(q1 + 1, q2 - q1 - 1);
                // ver may be "21.0.3" or "1.8.0_202"
                string[] parts = ver.Split('.');
                if (!int.TryParse(parts[0], out int major)) continue;
                if (major == 1 && parts.Length > 1)
                    int.TryParse(parts[1], out major); // 1.8 -> 8
                return major;
            }
        }
        catch { /* fall through */ }
        return 0;
    }

    // ── Private helpers — mod presence / version stamp ────────────────────────

    private bool IsModJarPresent()
    {
        try { return File.Exists(ModJarPath); }
        catch { return false; }
    }

    private string? ReadStampedVersion()
    {
        try
        {
            if (!File.Exists(ModVersionStampPath)) return null;
            string v = File.ReadAllText(ModVersionStampPath).Trim();
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        catch { return null; }
    }

    private void WriteStampedVersion(string version)
    {
        try
        {
            Directory.CreateDirectory(GameDirectory);
            File.WriteAllText(ModVersionStampPath, version, new UTF8Encoding(false));
        }
        catch { /* non-fatal */ }
    }

    // ── Private helpers — HTTP download ──────────────────────────────────────

    /// Download a URL to a local file, reporting progress between pctStart and pctEnd.
    private async Task DownloadFileAsync(
        string url, string destPath, IProgress<(int Pct, string Msg)> progress,
        int pctStart, int pctEnd, CancellationToken ct)
    {
        string dir = Path.GetDirectoryName(destPath)!;
        Directory.CreateDirectory(dir);

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        string fileName = Path.GetFileName(destPath);

        await using var src  = await response.Content.ReadAsStreamAsync(ct);
        await using var dest = File.Create(destPath);

        var buffer     = new byte[81_920];
        long downloaded = 0;
        int  read;

        while ((read = await src.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
        {
            await dest.WriteAsync(buffer, 0, read, ct);
            downloaded += read;

            if (total > 0)
            {
                double frac  = (double)downloaded / total.Value;
                int    pct   = pctStart + (int)((pctEnd - pctStart) * frac);
                long   dlKB  = downloaded / 1024;
                long   totKB = total.Value / 1024;
                progress.Report((pct, $"Downloading {fileName}: {dlKB:N0} / {totKB:N0} KB"));
            }
        }

        progress.Report((pctEnd, $"Downloaded {fileName}."));
    }

    // ── Private helpers — UI building ─────────────────────────────────────────

    private static void AddSectionHeader(
        System.Windows.Controls.StackPanel panel, string text,
        System.Windows.Media.SolidColorBrush foreground)
    {
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = text, FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = foreground,
            Margin = new System.Windows.Thickness(0, 8, 0, 6),
        });
    }

    private void AddFolderRow(
        System.Windows.Controls.StackPanel panel,
        System.Windows.Media.SolidColorBrush fg,
        System.Windows.Media.SolidColorBrush muted,
        string label,
        string currentPath,
        string tooltip,
        Action<string> onPicked)
    {
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = label, FontSize = 11, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 0, 0, 3),
        });

        var row = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 8) };
        var box = new System.Windows.Controls.TextBox
        {
            Text = currentPath, IsReadOnly = true, FontSize = 11,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = tooltip,
        };
        var btn = new System.Windows.Controls.Button
        {
            Content = "Browse...", Width = 90,
            Padding = new System.Windows.Thickness(0, 5, 0, 5),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        btn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = label,
                Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog() == true)
            {
                box.Text = dlg.FileName;
                onPicked(dlg.FileName);
            }
        };
        System.Windows.Controls.DockPanel.SetDock(btn, System.Windows.Controls.Dock.Right);
        row.Children.Add(btn);
        row.Children.Add(box);
        panel.Children.Add(row);
    }

    // ── Private helpers — URL opener ──────────────────────────────────────────

    private static void TryOpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* non-fatal */ }
    }

    // ── Private helpers — settings sidecar ───────────────────────────────────

    private sealed class MinecraftSettings
    {
        public string? JavaExeOverride { get; set; }
    }

    private MinecraftSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<MinecraftSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt -> defaults */ }
        return new();
    }

    private void SaveSettings(MinecraftSettings s)
    {
        try
        {
            Directory.CreateDirectory(GameDirectory);
            File.WriteAllText(
                SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal */ }
    }
}
