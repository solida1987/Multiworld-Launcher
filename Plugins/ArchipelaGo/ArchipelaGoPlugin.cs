using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LauncherV2.Core;

// IMPORTANT (real project has <UseWindowsForms>true</UseWindowsForms>):
// All WPF types are FULLY QUALIFIED to avoid CS0104 ambiguities.
// Do NOT add `using System.Windows.Controls;` / `using System.Windows.Media;` here.

namespace LauncherV2.Plugins.ArchipelaGo;

// =============================================================================
// ArchipelaGoPlugin -- mobile Archipelago client for Android
//
// Archipela-Go! is a React Native Android app that acts as an Archipelago
// multiworld client. Players connect their phone to an AP server and receive
// item notifications and location checks from the phone. The launcher can only
// link to the Android release (install via phone) -- it cannot install the APK
// locally. ConnectsItself = true (the phone app manages the AP connection).
//
// GitHub: https://github.com/aki665/react-native-archipelago
// =============================================================================

public sealed class ArchipelaGoPlugin : IGamePlugin
{
    // -- Constants -------------------------------------------------------------

    private const string GH_OWNER    = "aki665";
    private const string GH_REPO     = "react-native-archipelago";
    private const string RELEASES_URL =
        "https://github.com/aki665/react-native-archipelago/releases";

    // -- IGamePlugin -- Identity -----------------------------------------------

    public string GameId      => "archipela_go";
    public string DisplayName => "Archipela-Go!";
    public string Subtitle    => "Android - mobile AP client";
    public string ApWorldName => "Archipela-Go";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "archipela_go.png");

    public string ThemeAccentColor => "#2E7D32";
    public string[] GameBadges     => new[] { "Free", "Android", "Mobile" };

    public string Description =>
        "Archipela-Go! is a mobile Archipelago client for Android. " +
        "Connect your phone to an Archipelago multiworld server and receive " +
        "item notifications and send location checks directly from your Android device. " +
        "Install the APK from the GitHub releases page on your phone.";

    public string? VideoPreviewUrl  => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // -- Version state ---------------------------------------------------------

    // Mobile app -- not installable by the PC launcher.
    public string? InstalledVersion { get; private set; } = null;
    public string? AvailableVersion { get; private set; } = null;

    /// Not installed locally (Android APK only).
    public bool IsInstalled => false;
    public bool IsRunning   => false;

    // -- IGamePlugin -- Properties ---------------------------------------------

    public string GameDirectory { get; set; } = string.Empty;
    public bool ConnectsItself   => true;
    public bool SupportsStandalone => false;

    // -- AP bridge events (inert — ConnectsItself = true) ----------------------
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
    public event Action<int>?    GameExited;
#pragma warning restore CS0067

    // -- Lifecycle -- CheckForUpdate -------------------------------------------

    public Task CheckForUpdateAsync(CancellationToken ct = default)
        => Task.CompletedTask;   // Android app; version not tracked by PC launcher

    // -- Lifecycle -- InstallOrUpdate ------------------------------------------

    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((50, "Opening Archipela-Go! releases page..."));
        OpenUrl(RELEASES_URL);
        progress.Report((100,
            "Download the APK from the releases page and install it on your Android device."));
        return Task.CompletedTask;
    }

    // -- Lifecycle -- Verify ---------------------------------------------------

    public Task<bool> VerifyInstallAsync(CancellationToken ct = default)
        => Task.FromResult(false);  // cannot verify Android install from PC

    // -- Lifecycle -- Launch ---------------------------------------------------

    /// Opens the GitHub releases page so the user can download on their phone.
    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        OpenUrl(RELEASES_URL);
        return Task.CompletedTask;
    }

    public Task StopAsync() => Task.CompletedTask;

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
            System.Windows.Media.Color.FromRgb(0x2E, 0x7D, 0x32));

        var panel = new System.Windows.Controls.StackPanel
            { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        panel.Children.Add(MakeLabel("ABOUT", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Archipela-Go! is an Android app that acts as an Archipelago multiworld client. " +
                   "Install the APK on your Android device to connect to any AP server from your phone. " +
                   "Click the link below to go to the releases page and download the latest APK.",
            FontSize     = 11,
            Foreground   = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 12),
        });

        panel.Children.Add(MakeLabel("INSTALL", muted));

        foreach (string step in new[]
        {
            "1. Click 'Download APK' below to open the releases page.",
            "2. Download the latest .apk file on your Android device.",
            "3. Enable 'Install from unknown sources' in Android settings.",
            "4. Open the APK file to install Archipela-Go! on your phone.",
            "5. Connect to your AP server from the app.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text         = step,
                FontSize     = 11,
                Foreground   = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin       = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        panel.Children.Add(MakeLabel("LINKS", muted));

        var btn = new System.Windows.Controls.Button
        {
            Content             = "Download APK on GitHub ↗",
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Padding             = new System.Windows.Thickness(0, 2, 0, 2),
            Background          = System.Windows.Media.Brushes.Transparent,
            BorderThickness     = new System.Windows.Thickness(0),
            FontSize            = 12,
            Margin              = new System.Windows.Thickness(0, 0, 0, 4),
            Foreground          = accent,
            Cursor              = System.Windows.Input.Cursors.Hand,
        };
        btn.Click += (_, _) => OpenUrl(RELEASES_URL);
        panel.Children.Add(btn);

        return panel;
    }

    // -- News feed -------------------------------------------------------------

    public Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
        => Task.FromResult(Array.Empty<NewsItem>());

    // -- Private helpers -------------------------------------------------------

    private static System.Windows.Controls.TextBlock MakeLabel(
        string text, System.Windows.Media.SolidColorBrush color) => new()
    {
        Text       = text,
        FontSize   = 10,
        FontWeight = System.Windows.FontWeights.SemiBold,
        Foreground = color,
        Margin     = new System.Windows.Thickness(0, 0, 0, 8),
    };

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }
}
