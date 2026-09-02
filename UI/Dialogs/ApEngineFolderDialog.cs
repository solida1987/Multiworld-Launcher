using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core.Archipelago;

namespace LauncherV2.UI.Dialogs;

///
/// "We cannot find your Archipelago — where is it?"
///
/// London drives the copy of Archipelago the player already has, and it can
/// usually find it. When it cannot, everything that depends on it goes quiet:
/// the worlds do not update, the YAML forms are never rewritten, and the
/// buttons that do those things fail with a message about setting something up
/// somewhere the player cannot find. Asking once, out loud, is the fix.
///
/// The folder is checked before it is accepted, so a player cannot leave here
/// having pointed London at something that will not work — and if it will not
/// work, they are told which part is missing.
///
public sealed class ApEngineFolderDialog : Window
{
    private static readonly Brush Ink  = new SolidColorBrush(Color.FromRgb(0xE6, 0xEA, 0xF2));
    private static readonly Brush Dim  = new SolidColorBrush(Color.FromRgb(0x8A, 0x93, 0xA8));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xE0, 0x9A, 0x3C));

    private readonly TextBlock _problem;

    /// The folder the player settled on, or null if they closed this.
    public string? Chosen { get; private set; }

    private ApEngineFolderDialog(Window? owner, string why)
    {
        Owner = owner;
        Title = "Where is your Archipelago installation?";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = owner is null
            ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x11, 0x14, 0x1C));
        ResizeMode = ResizeMode.NoResize;

        var root = new StackPanel { Margin = new Thickness(22) };

        root.Children.Add(new TextBlock
        {
            Text = "London cannot find your Archipelago installation",
            Foreground = Ink, FontSize = 16, FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
        });

        root.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 4),
            Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Text = why,
        });

        // Said concretely, because "your Archipelago folder" is not something
        // everyone can point at, while a file name is.
        root.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 14),
            Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Text = "Pick the folder that contains ArchipelagoGenerate.exe. On most "
                 + "machines that is C:\\ProgramData\\Archipelago, but it is wherever "
                 + "you installed it. London only reads and writes inside that folder; "
                 + "it never changes your host.yaml.",
        });

        _problem = new TextBlock
        {
            Foreground = Warn, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12), Visibility = Visibility.Collapsed,
        };
        root.Children.Add(_problem);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var later = new Button
        {
            Content = "Not now",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 0),
        };
        later.Click += (_, _) => { Chosen = null; DialogResult = false; };
        var pick = new Button
        {
            Content = "Choose folder…",
            Padding = new Thickness(14, 6, 14, 6),
            IsDefault = true,
        };
        pick.Click += (_, _) => Browse();
        buttons.Children.Add(later);
        buttons.Children.Add(pick);
        root.Children.Add(buttons);

        Content = root;
    }

    private void Browse()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select your Archipelago folder (the one with ArchipelagoGenerate.exe)",
        };
        string? start = ApEngineLocation.StartingPointForBrowse(ApEngineLocation.Current());
        if (start != null) dlg.InitialDirectory = start;
        if (dlg.ShowDialog(this) != true) return;

        // Checked here rather than accepted and discovered to be wrong later,
        // when the failure would show up as a button that does nothing.
        var report = ApEngineLocation.Check(dlg.FolderName);
        if (report.Usable)
        {
            Chosen = dlg.FolderName;
            DialogResult = true;
            return;
        }
        _problem.Text = report.Exists
            ? "That folder cannot be used: " + string.Join("; ", report.Problems)
            : "There is no Archipelago installation in that folder — "
            + "ArchipelagoGenerate.exe is not there.";
        _problem.Visibility = Visibility.Visible;
    }

    ///
    /// Ask, and remember the answer. Returns the located install, or null when
    /// the player chose not to say — in which case nothing is saved and they
    /// are asked again next time it actually matters.
    ///
    public static ApEngineLocation.Where? Ask(Window? owner, string why)
    {
        var dlg = new ApEngineFolderDialog(owner, why);
        dlg.ShowDialog();
        if (dlg.Chosen is not { Length: > 0 } picked) return null;
        ApEngineLocation.Choose(picked);
        return ApEngineLocation.Current();
    }
}
