using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LauncherV2.Core;

// IMPORTANT (real project has <UseWindowsForms>true</UseWindowsForms>):
// WPF UI types that collide with WinForms are FULLY QUALIFIED below
// (System.Windows.Controls.*, System.Windows.Media.*, System.Windows.Thickness,
// System.Windows.FontWeights, System.Windows.HorizontalAlignment,
// System.Windows.TextWrapping, ...) to avoid CS0104 ambiguities. Do NOT add
// `using System.Windows.Controls;` / `using System.Windows.Media;` here.

namespace LauncherV2.Plugins.Apquest;

// =============================================================================
// ApquestPlugin -- web-browser launch for "APQuest", a Pokemon-like browser-
// based Archipelago RPG adventure game bundled with the main Archipelago dist.
//
// INTEGRATION MODEL
// -----------------
//   * ConnectsItself = true: the in-browser game client speaks to the AP server
//     directly. The launcher must NOT hold its own ApClient on the same slot
//     while the player is in the browser.
//   * SupportsStandalone = false: the game is built around AP multiworld play;
//     no standalone offline mode is documented.
//   * IsInstalled = true: web-based game, no local installation required.
//   * LaunchAsync: opens the official Archipelago APQuest page in the default
//     system browser.
//   * GameDirectory = string.Empty: no local files to manage.
//
// BUNDLED AP WORLD
// ----------------
//   APQuest ships as a bundled world inside the main Archipelago distribution
//   (worlds/apquest/). The exact AP game string is "APQuest" (verified
//   against worlds/apquest/world.py and web_world.py in the AP-main repo).
//   There is no separate GitHub release or download to track -- AP.gg hosts
//   the web client.
//
// SETUP FLOW
// ----------
//   1. Generate your multiworld on archipelago.gg with an APQuest slot.
//   2. Click Play -- this opens the APQuest client page on archipelago.gg.
//   3. Enter your server, slot name, and password to connect.
// =============================================================================

public sealed class ApquestPlugin : IGamePlugin
{
    // -- Constants -------------------------------------------------------------

    private const string AP_GAME_PAGE = "https://archipelago.gg/games/APQuest";
    private const string AP_SITE      = "https://archipelago.gg";

    // -- IGamePlugin -- Identity -----------------------------------------------

    public string GameId        => "apquest";
    public string DisplayName   => "APQuest";
    public string Subtitle      => "Web Browser - built-in AP client";

    /// EXACT AP game string -- verified against worlds/apquest/world.py
    /// and worlds/apquest/web_world.py (game = "APQuest") in the AP-main repo.
    public string ApWorldName   => "APQuest";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "apquest.png");

    public string ThemeAccentColor => "#4A9E4A";   // grass green, matching APQuest grassFlowers theme
    public string[] GameBadges     => new[] { "Free", "Web Browser", "Bundled AP" };

    public string Description =>
        "APQuest is a Pokemon-like browser-based Archipelago RPG adventure game bundled " +
        "directly with Archipelago. Explore a top-down world, battle enemies, and unlock " +
        "new abilities and areas as items arrive from other worlds. The game runs entirely " +
        "in your browser -- no download needed. Click Play to open the game on archipelago.gg " +
        "and enter your server details to connect.";

    public string? VideoPreviewUrl  => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // -- Version state ---------------------------------------------------------

    // APQuest is a bundled AP world served from archipelago.gg -- there is no
    // versioned local install. InstalledVersion is fixed to "web"; AvailableVersion
    // is not meaningful (the AP platform determines the version in use).
    public string? InstalledVersion { get; private set; } = "web";
    public string? AvailableVersion { get; private set; }

    /// Always true -- no local installation is needed for a web-based game.
    public bool IsInstalled => true;
    public bool IsWebBased => true;

    /// Never running as a launcher-tracked local process.
    public bool IsRunning => false;

    // -- IGamePlugin -- Properties ---------------------------------------------

    /// No local files -- GameDirectory is not used for this web game.
    public string GameDirectory { get; set; } = string.Empty;

    /// The in-browser AP client owns the slot connection.
    /// The launcher must NOT also connect its own ApClient to this slot.
    public bool ConnectsItself => true;

    /// No meaningful standalone play without an AP server.
    public bool SupportsStandalone => false;

    // -- AP bridge events ------------------------------------------------------
    // ConnectsItself = true: the browser client speaks to the AP server directly.
    // These events exist for interface compatibility; they are never raised.
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
    public event Action<int>?    GameExited;
#pragma warning restore CS0067

    // -- Lifecycle -- CheckForUpdate -------------------------------------------

    public Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        // Web game served from archipelago.gg -- no release to check.
        InstalledVersion = "web";
        AvailableVersion = null;
        return Task.CompletedTask;
    }

    // -- Lifecycle -- InstallOrUpdate ------------------------------------------

    /// No installation needed -- opens the AP game page so the player can
    /// verify the game is accessible.
    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((50, "Opening APQuest on archipelago.gg..."));
        OpenUrl(AP_GAME_PAGE);
        progress.Report((100,
            "APQuest is a browser-based game -- no installation needed. " +
            "Click Play to open the game on archipelago.gg."));
        return Task.CompletedTask;
    }

    // -- Lifecycle -- Verify ---------------------------------------------------

    public Task<bool> VerifyInstallAsync(CancellationToken ct = default)
        => Task.FromResult(true);   // web game is always available

    // -- Lifecycle -- Launch ---------------------------------------------------

    /// Opens the APQuest client page on archipelago.gg in the default browser.
    /// The in-browser AP client handles the server/slot/password connection.
    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        OpenUrl(AP_GAME_PAGE);
        return Task.CompletedTask;
    }

    /// Nothing to stop -- the launcher cannot close a browser tab.
    public Task StopAsync()
        => Task.CompletedTask;

    // -- AP bridge -- inert (ConnectsItself = true) ----------------------------

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // -- Settings UI -----------------------------------------------------------

    public UIElement? CreateSettingsPanel()
    {
        var muted  = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg     = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var accent = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF));

        var panel = new System.Windows.Controls.StackPanel
            { Margin = new Thickness(0, 0, 0, 20) };

        // -- How to play -------------------------------------------------------
        panel.Children.Add(MakeLabel("HOW TO PLAY", muted));

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "APQuest is a Pokemon-like browser-based Archipelago RPG adventure game. " +
                "It is bundled with Archipelago and runs entirely in your browser -- there is " +
                "nothing to download or install. Click Play to open the game on archipelago.gg, " +
                "then enter your server address, slot name, and password to connect.",
            FontSize     = 11,
            Foreground   = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 12),
        });

        foreach (string step in new[]
        {
            "1. Generate a multiworld on archipelago.gg that includes an APQuest slot.",
            "2. Click Play -- your browser will open the APQuest game page.",
            "3. Enter your server address, port, slot name, and password to start playing.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text         = step,
                FontSize     = 11,
                Foreground   = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 5),
            });
        }

        // -- Links -------------------------------------------------------------
        panel.Children.Add(MakeLabel("LINKS", muted));

        foreach (var (label, url) in new[]
        {
            ("APQuest on archipelago.gg ?", AP_GAME_PAGE),
            ("Archipelago Official ?",      AP_SITE),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content             = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding             = new Thickness(0, 2, 0, 2),
                Background          = System.Windows.Media.Brushes.Transparent,
                BorderThickness     = new Thickness(0),
                FontSize            = 12,
                Margin              = new Thickness(0, 0, 0, 4),
                Foreground          = accent,
                Cursor              = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) => OpenUrl(u);
            panel.Children.Add(btn);
        }

        return panel;
    }

    // -- News feed -------------------------------------------------------------

    public Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
        => Task.FromResult(Array.Empty<NewsItem>());

    // -- Private helpers -------------------------------------------------------

    private static System.Windows.Controls.TextBlock MakeLabel(
        string text,
        System.Windows.Media.SolidColorBrush color) => new()
    {
        Text       = text,
        FontSize   = 10,
        FontWeight = System.Windows.FontWeights.SemiBold,
        Foreground = color,
        Margin     = new Thickness(0, 0, 0, 8),
    };

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* non-fatal -- browser launch failure is best-effort */ }
    }
}

