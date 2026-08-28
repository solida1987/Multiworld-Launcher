using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LauncherV2.UI.Controls;

// PromptDialog — one question, one text box, one answer.
//
// Built in code rather than XAML because it is four controls, and because the
// first thing that needed it (naming a library folder) should not have to
// wait for a designer file. Styled to match ConfirmDialog by hand; if a third
// dialog ever needs the same skeleton, promote the common part instead of
// copying it again.
public sealed class PromptDialog : Window
{
    private readonly TextBox _input;
    private string? _result;

    private PromptDialog(Window owner, string title, string question, string initial)
    {
        Owner                 = owner;
        Title                 = title;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent         = SizeToContent.Height;
        Width                 = 380;
        ResizeMode            = ResizeMode.NoResize;
        WindowStyle           = WindowStyle.ToolWindow;
        Background            = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x24));

        var stack = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        stack.Children.Add(new TextBlock
        {
            Text         = question,
            Foreground   = new SolidColorBrush(Color.FromRgb(0xD8, 0xDC, 0xEE)),
            FontSize     = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 10),
        });
        _input = new TextBox
        {
            Text        = initial,
            FontSize    = 12,
            Padding     = new Thickness(6, 4, 6, 4),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = new SolidColorBrush(Color.FromRgb(0xD8, 0xDC, 0xEE)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Accept();
            if (e.Key == Key.Escape) DialogResult = false;
        };
        stack.Children.Add(_input);

        var buttons = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, 14, 0, 0),
        };
        var ok = new Button
        {
            Content    = "OK",
            Width      = 74,
            Padding    = new Thickness(0, 4, 0, 4),
            Margin     = new Thickness(0, 0, 8, 0),
            IsDefault  = true,
            Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xC5, 0x18)),
            Foreground = Brushes.Black,
            FontWeight = FontWeights.SemiBold,
        };
        ok.Click += (_, _) => Accept();
        var cancel = new Button
        {
            Content    = "Cancel",
            Width      = 74,
            Padding    = new Thickness(0, 4, 0, 4),
            IsCancel   = true,
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1F, 0x33)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xDC, 0xEE)),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        stack.Children.Add(buttons);
        Content = stack;

        Loaded += (_, _) => { _input.Focus(); _input.SelectAll(); };
    }

    private void Accept()
    {
        _result = _input.Text;
        DialogResult = true;
    }

    /// Returns the entered text, or null when the player cancelled. An empty
    /// answer comes back as null too -- no caller wants a nameless folder.
    public static string? Show(Window owner, string title, string question, string initial = "")
    {
        var dlg = new PromptDialog(owner, title, question, initial);
        bool? ok = dlg.ShowDialog();
        string? text = dlg._result?.Trim();
        return ok == true && !string.IsNullOrWhiteSpace(text) ? text : null;
    }
}
