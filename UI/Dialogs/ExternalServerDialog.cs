using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core.Archipelago;

namespace LauncherV2.UI.Dialogs;

/// Name a server somebody else is hosting.
///
/// Two fields and nothing else: where it is, and what you want to call it. No
/// slot name here — that is the point of the change. A server exists first,
/// and the slots you were given are added to it afterwards, one card at a
/// time, on the Join tab itself.
///
/// ⚠ The dialog this replaces asked for the address AND a slot in one breath,
/// so a server could not exist until it had a slot, and a second slot meant
/// typing the address again. Worse, what came out was appended to whatever
/// seed happened to be showing — a player with slots on three servers saw all
/// of them in one unrelated pile with nothing saying which was which.
public static class ExternalServerDialog
{
    public static ExternalServer? Show(Window? owner)
    {
        var win = new Window
        {
            Title = "Add a server",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner == null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize,
            Background = Res("BrushBackground", "#0B0E14"),
        };

        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        win.Content = root;

        root.Children.Add(new TextBlock
        {
            Text = "A session somebody else is hosting",
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = Res("BrushText", "#CCD0E0"),
            Margin = new Thickness(0, 0, 0, 4),
        });
        root.Children.Add(new TextBlock
        {
            Text = "Give it a name you will recognise. Its slots are added on the "
                 + "Join tab afterwards, and every one of them will sit under this "
                 + "name.",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Foreground = Res("BrushText", "#CCD0E0"),
            Margin = new Thickness(0, 0, 0, 16),
        });

        TextBox Field(string label, string hint, string preset = "")
        {
            root.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Opacity = 0.6,
                Foreground = Res("BrushText", "#CCD0E0"),
                Margin = new Thickness(0, 0, 0, 4),
            });
            var box = new TextBox
            {
                Text = preset,
                Padding = new Thickness(8, 6, 8, 6),
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 3),
            };
            root.Children.Add(box);
            root.Children.Add(new TextBlock
            {
                Text = hint,
                FontSize = 10.5,
                Opacity = 0.55,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Res("BrushText", "#CCD0E0"),
                Margin = new Thickness(0, 0, 0, 14),
            });
            return box;
        }

        var address = Field("ADDRESS",
            "Host and port, as you were given it — for example "
          + "archipelago.gg:38281.");
        var name = Field("NAME",
            "Anything that tells you which game this is. It is only shown to you.");
        var password = Field("PASSWORD",
            "Leave empty unless the host set one. It is remembered for every "
          + "slot you add to this server.");

        var error = new TextBlock
        {
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
            Margin = new Thickness(0, 0, 0, 10),
        };
        root.Children.Add(error);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var cancel = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(0, 0, 8, 0),
        };
        var save = new Button
        {
            Content = "Add server",
            Padding = new Thickness(16, 6, 16, 6),
            IsDefault = true,
        };
        row.Children.Add(cancel);
        row.Children.Add(save);
        root.Children.Add(row);

        ExternalServer? made = null;

        cancel.Click += (_, _) => win.Close();
        save.Click += (_, _) =>
        {
            string addr = address.Text.Trim();
            if (addr.Length == 0)
            {
                error.Text = "An address is needed — the host and port you were given.";
                error.Visibility = Visibility.Visible;
                return;
            }
            // ⚠ Not verified here. A server that is asleep right now is still
            // the right address, and refusing to save it would mean typing it
            // again later. The slots probe when they are added, and say so
            // there.
            made = ExternalServerStore.Add(name.Text, addr, password.Text);
            win.Close();
        };

        address.Focus();
        win.ShowDialog();
        return made;
    }

    private static Brush Res(string key, string fallback)
    {
        try
        {
            if (Application.Current?.TryFindResource(key) is Brush b) return b;
        }
        catch (Exception) { }
        return (Brush)new BrushConverter().ConvertFrom(fallback)!;
    }
}
