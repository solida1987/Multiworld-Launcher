using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;

namespace LauncherV2.UI.Pages;

/// <summary>
/// Lightweight splash screen shown during launcher startup.
/// Call <see cref="FadeOutAsync"/> when the main window is ready to take over.
/// </summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();

        // Display the actual assembly version
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        if (ver != null)
            TxtVersion.Text = $"v{ver.Major}.{ver.Minor}.{ver.Build}";
    }

    /// Shows a status line below the loading dots (e.g. "Updating…  42%").
    /// Passes through the Dispatcher so it is safe to call from any thread.
    public void SetUpdateStatus(string text)
    {
        Dispatcher.Invoke(() =>
        {
            TxtUpdateStatus.Text       = text;
            TxtUpdateStatus.Visibility = string.IsNullOrEmpty(text)
                ? System.Windows.Visibility.Collapsed
                : System.Windows.Visibility.Visible;
        });
    }

    /// <summary>
    /// Fades the splash window out over 300 ms, then closes it.
    /// Awaitable — callers can await this before activating the main window.
    /// </summary>
    public Task FadeOutAsync()
    {
        var tcs = new TaskCompletionSource<bool>();

        var anim = new DoubleAnimation(1, 0,
            new Duration(TimeSpan.FromMilliseconds(300)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        anim.Completed += (_, _) =>
        {
            Close();
            tcs.TrySetResult(true);
        };

        Dispatcher.Invoke(() => BeginAnimation(OpacityProperty, anim));
        return tcs.Task;
    }
}
