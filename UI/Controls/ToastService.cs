using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

// (WPF/WinForms disambiguation handled in GlobalUsings.cs. Panel is not pinned
//  there, so it is fully qualified below to avoid the WinForms clash.)

namespace LauncherV2.UI.Controls;

// ═══════════════════════════════════════════════════════════════════════════════
// ToastService — lightweight transient notifications inside the main window.
//
// Stacked toasts bottom-right (icon + title + optional body) with an accent-
// coloured left edge keyed to the toast kind. Fade-in, auto-dismiss after
// ~4.5 s, click anywhere to dismiss, max 4 stacked (oldest is dropped).
// Dispatcher-safe: Show() may be called from any thread.
//
// The achievement toast (AchievementToast window) is intentionally separate —
// this service handles generic launcher events (updates, connection loss, …).
// ═══════════════════════════════════════════════════════════════════════════════

/// Toast severity — selects the accent colour + icon.
/// Info = gold, Success = green, Warning = amber, Error = red.
public enum ToastKind { Info, Success, Warning, Error }

public static class ToastService
{
    private const int    MaxStacked  = 4;
    private const double ShowSeconds = 4.5;

    private static System.Windows.Controls.Panel? _host;

    /// Called once by MainWindow after InitializeComponent with the host panel
    /// (a StackPanel anchored bottom-right of the window content).
    public static void Initialize(System.Windows.Controls.Panel host) => _host = host;

    /// Show a transient toast. Safe to call from any thread.
    public static void Show(string title, string body = "", ToastKind kind = ToastKind.Info)
    {
        var app = Application.Current;
        if (app == null) return;
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(() => Show(title, body, kind));
            return;
        }
        if (_host == null) return;

        // Cap the stack — drop the oldest toast immediately
        while (_host.Children.Count >= MaxStacked)
            _host.Children.RemoveAt(0);

        var (accent, icon) = KindStyle(kind);
        var toast = BuildToast(title, body, accent, icon);
        _host.Children.Add(toast);

        // Fade-in
        toast.Opacity = 0;
        toast.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));

        // Auto-dismiss
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(ShowSeconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            FadeOutAndRemove(toast);
        };
        timer.Start();

        // Click anywhere on the toast dismisses it instantly
        toast.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            timer.Stop();
            Remove(toast);
        };
    }

    // ── Kind → accent colour + icon ───────────────────────────────────────────

    private static (Color Accent, string Icon) KindStyle(ToastKind kind) => kind switch
    {
        ToastKind.Success => (Color.FromRgb(0x4C, 0xAF, 0x50), "✔"),
        ToastKind.Warning => (Color.FromRgb(0xF5, 0x9E, 0x0B), "⚠"),
        ToastKind.Error   => (Color.FromRgb(0xD9, 0x4A, 0x4A), "✖"),
        _                 => (Color.FromRgb(0xCC, 0xA8, 0x00), "ℹ"),
    };

    // ── Visual construction ───────────────────────────────────────────────────

    private static Border BuildToast(string title, string body, Color accent, string icon)
    {
        var outer = new Border
        {
            Margin          = new Thickness(0, 8, 0, 0),
            CornerRadius    = new CornerRadius(6),
            Background      = new SolidColorBrush(Color.FromArgb(0xF2, 0x14, 0x17, 0x20)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            BorderThickness = new Thickness(1),
            Cursor          = Cursors.Hand,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black, BlurRadius = 14, Opacity = 0.5, ShadowDepth = 2,
            },
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition
            { Width = new GridLength(1, GridUnitType.Star) });

        // Accent-coloured left edge (3 px)
        var edge = new Border
        {
            Background   = new SolidColorBrush(accent),
            CornerRadius = new CornerRadius(5, 0, 0, 5),
        };
        Grid.SetColumn(edge, 0);
        grid.Children.Add(edge);

        // Kind icon
        var iconTb = new TextBlock
        {
            Text              = icon,
            FontSize          = 17,
            Foreground        = new SolidColorBrush(accent),
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(12, 0, 0, 0),
        };
        Grid.SetColumn(iconTb, 1);
        grid.Children.Add(iconTb);

        // Title + optional body
        var textStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(10, 10, 14, 10),
        };
        textStack.Children.Add(new TextBlock
        {
            Text         = title,
            FontSize     = 12,
            FontWeight   = FontWeights.Bold,
            Foreground   = new SolidColorBrush(Color.FromRgb(0xE0, 0xE4, 0xF0)),
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrEmpty(body))
        {
            textStack.Children.Add(new TextBlock
            {
                Text         = body,
                FontSize     = 10,
                Foreground   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99)),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 2, 0, 0),
            });
        }
        Grid.SetColumn(textStack, 2);
        grid.Children.Add(textStack);

        outer.Child = grid;
        return outer;
    }

    // ── Dismissal ─────────────────────────────────────────────────────────────

    private static void FadeOutAndRemove(Border toast)
    {
        var fade = new DoubleAnimation(toast.Opacity, 0, TimeSpan.FromMilliseconds(400));
        fade.Completed += (_, _) => Remove(toast);
        toast.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private static void Remove(Border toast)
        => _host?.Children.Remove(toast);
}
