using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LauncherV2.Core.Plugins;

// "There is an update to your plugin. Do you want to install it?"
//
// One screen, one decision, and it names what it is about to fetch and from
// where -- the same standard the "Add plugin" dialog holds itself to, because
// an update is an install of somebody else's code just as much as the first one
// was.
//
// Saying yes here does NOT complete the update. It downloads and verifies, and
// then the ordinary consent dialog opens with the new version's hash and
// declarations. Two prompts for one update is deliberate: the first asks "do
// you want a newer build", the second asks "do you accept THIS build".
public sealed class PluginUpdateDialog : Window
{
    private static readonly Brush Bg     = Frozen(0x11, 0x14, 0x1C);
    private static readonly Brush Card   = Frozen(0x1A, 0x1E, 0x2A);
    private static readonly Brush Text   = Frozen(0xCC, 0xD0, 0xE0);
    private static readonly Brush Muted  = Frozen(0x72, 0x7A, 0x99);
    private static readonly Brush Gold   = Frozen(0xE8, 0xA0, 0x18);
    private static readonly Brush Red    = Frozen(0xE5, 0x5A, 0x5A);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    private readonly PluginUpdater.Available _update;
    private readonly TextBlock   _status = new()
    {
        Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 12, 0, 0),
    };
    private readonly ProgressBar _bar = new()
    {
        Height = 14, Minimum = 0, Maximum = 1, Foreground = Gold,
        Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed,
    };
    private readonly Button _install = Btn("Download and install", true);
    private readonly Button _later   = Btn("Not now", false);

    /// The downloaded, checksum-verified package -- null when the player said
    /// no, closed the window, or the download failed.
    public string? DownloadedPackagePath { get; private set; }

    /// Ask about one update. Returns the verified package path, or null.
    public static string? Ask(Window? owner, PluginUpdater.Available update)
    {
        var dlg = new PluginUpdateDialog(update) { Owner = owner };
        dlg.ShowDialog();
        return dlg.DownloadedPackagePath;
    }

    private PluginUpdateDialog(PluginUpdater.Available update)
    {
        _update = update;

        Title = "Plugin update";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Bg;

        var root = new StackPanel { Margin = new Thickness(22) };

        root.Children.Add(new TextBlock
        {
            Text = "An update is available",
            Foreground = Gold, FontSize = 19, FontWeight = FontWeights.Bold,
        });
        root.Children.Add(new TextBlock
        {
            Text = update.DisplayName,
            Foreground = Text, FontSize = 13, Margin = new Thickness(0, 2, 0, 14),
        });

        var card = new Border
        {
            Background = Card, CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 4),
        };
        var inner = new StackPanel();
        inner.Children.Add(Row("Installed", update.InstalledVersion));
        inner.Children.Add(Row("New version", update.NewVersion.ToString()));
        inner.Children.Add(Row("Comes from", update.Source.Host));
        card.Child = inner;
        root.Children.Add(card);

        root.Children.Add(new TextBlock
        {
            Text = "The file is downloaded from this plugin's own project and checked "
                 + "against the checksum published there. Nothing is replaced yet — you "
                 + "will be shown what the new version declares it does, and can still "
                 + "say no.",
            Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        });

        root.Children.Add(_status);
        root.Children.Add(_bar);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        buttons.Children.Add(_later);
        buttons.Children.Add(_install);
        root.Children.Add(buttons);

        Content = root;

        _later.Click   += (_, _) => Close();
        _install.Click += async (_, _) => await RunAsync();
    }

    private static UIElement Row(string label, string value)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var l = new TextBlock { Text = label, Foreground = Muted, FontSize = 12 };
        var v = new TextBlock
        {
            Text = value, Foreground = Text, FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(v, 1);
        g.Children.Add(l);
        g.Children.Add(v);
        return g;
    }

    private static Button Btn(string text, bool primary) => new()
    {
        Content = text, Padding = new Thickness(16, 8, 16, 8),
        Margin = new Thickness(8, 0, 0, 0), MinWidth = 120,
        Background  = primary ? Frozen(0x2A, 0x36, 0x52) : Frozen(0x1E, 0x22, 0x30),
        Foreground  = primary ? Gold : Text,
        BorderBrush = Frozen(0x32, 0x3A, 0x50),
        Cursor = System.Windows.Input.Cursors.Hand,
    };

    private async Task RunAsync()
    {
        _install.IsEnabled = _later.IsEnabled = false;
        _bar.Visibility = Visibility.Visible;
        _status.Foreground = Muted;
        _status.Text = "Downloading…";

        var progress = new Progress<PluginUpdater.Progress>(p =>
        {
            _bar.Value = p.Fraction ?? 0;
            _status.Text = p.BytesTotal > 0
                ? $"Downloading… {p.BytesDone / 1024.0:N0} of {p.BytesTotal / 1024.0:N0} KB"
                : "Downloading…";
        });

        try
        {
            DownloadedPackagePath = await PluginUpdater.DownloadAsync(
                _update, progress, CancellationToken.None);
            Close();
        }
        catch (Exception ex)
        {
            _bar.Visibility = Visibility.Collapsed;
            _status.Foreground = Red;
            _status.Text = ex.Message;
            _install.Content = "Try again";
            _install.IsEnabled = _later.IsEnabled = true;
        }
    }
}
