using System;
using System.Windows;
using System.Windows.Media.Animation;
using LauncherV2.Core;

namespace LauncherV2.UI.Controls;

/// <summary>
/// Steam-style achievement toast popup.
/// Slides in from the bottom-right, stays for 4 seconds, then fades out.
/// Never steals focus (ShowActivated=False — it used to yank keyboard focus
/// out of the game), and simultaneous unlocks stack upwards instead of
/// painting on top of each other (a first session can grant 2–3 at once).
/// </summary>
public partial class AchievementToast : Window
{
    private const double MarginRight  = 18;
    private const double MarginBottom = 18;
    private const double StackGap     = 8;
    private const double ShowSeconds  = 4.0;

    /// Live (visible) toasts, bottom-most first — each new toast slots in
    /// above the ones already showing. UI thread only.
    private static readonly List<AchievementToast> _open = new();

    public AchievementToast(AchievementDefinition def)
    {
        InitializeComponent();
        TxtIcon.Text  = def.Icon;
        TxtTitle.Text = def.Title;
        TxtDesc.Text  = def.Description;

        Loaded += OnLoaded;
        Closed += (_, _) => _open.Remove(this);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Position: bottom-right of the primary screen, stacked above any
        // toast already on screen.
        int slot = _open.Count;
        _open.Add(this);

        var screen = SystemParameters.WorkArea;
        Left = screen.Right  - Width - MarginRight;
        Top  = screen.Bottom - Height - MarginBottom
               - slot * (Height + StackGap);

        // Fade-in + auto-close
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350));
        BeginAnimation(OpacityProperty, fadeIn);

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(ShowSeconds)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            FadeOutAndClose();
        };
        timer.Start();
    }

    private void FadeOutAndClose()
    {
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(500));
        fadeOut.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fadeOut);
    }
}
