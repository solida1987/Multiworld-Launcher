using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core;
using LauncherV2.Core.Archipelago;

namespace LauncherV2.UI.Dialogs;

/// Sign in to one slot on a server somebody else is running.
///
/// The player types an address and the name they were given. Nothing else —
/// not the game, which is the whole point: they were told "you are Marco on
/// archipelago.gg:38281", and that is what they should be able to type.
/// London asks the server what that slot plays.
///
/// Adding five slots means opening this five times, which is deliberate. Each
/// one is a separate connection to a separate game, and batching them behind
/// one form would hide which of them failed.
public static class ExternalSlotDialog
{
    public static ExternalSlot? Show(Window? owner)
    {
        var win = new Window
        {
            Title = "Join a session hosted elsewhere",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner == null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize,
            Background = Brush("BrushBackground", "#0B0E14"),
        };

        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };

        root.Children.Add(new TextBlock
        {
            Text = "Join a session hosted elsewhere",
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            Foreground = Brush("BrushAccent", "#E0A82E"),
        });
        root.Children.Add(new TextBlock
        {
            Text = "Somebody else is running the server. Type where it is and the "
                 + "slot name you were given — London asks the server what that "
                 + "slot plays and sets the game up from there.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 16),
            FontSize = 12.5,
            Foreground = Brush("BrushMuted", "#8A93AD"),
        });

        // The example lives in the hint, NOT as pre-filled text: a player who
        // forgets to replace pre-filled text probes a stranger's address.
        var txtAddress = Field(root, "Server", "",
            "Host and port, e.g. archipelago.gg:38281. A bare address tries a "
            + "secure connection first.");
        var txtSlot = Field(root, "Slot name", "",
            "Exactly as the host wrote it — capitals and spaces count.");
        var txtPassword = Field(root, "Password", "",
            "Leave empty unless the host set one.");

        var status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0),
            FontSize = 12,
            Foreground = Brush("BrushMuted", "#8A93AD"),
            Text = "Nothing is saved until the server accepts the name.",
        };
        root.Children.Add(status);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        var btnCancel = new Button { Content = "Cancel", Padding = new Thickness(16, 7, 16, 7),
                                     Margin = new Thickness(0, 0, 8, 0) };
        var btnAdd = new Button { Content = "Check and add", Padding = new Thickness(16, 7, 16, 7),
                                  IsDefault = true };
        // Styled when the app's resources are loaded, plain buttons otherwise.
        // The lookup itself must never be what breaks the dialog.
        if (Application.Current?.TryFindResource("BtnSecondaryStyle") is Style secondary)
            btnCancel.Style = secondary;
        if (Application.Current?.TryFindResource("BtnPlayStyle") is Style primary)
            btnAdd.Style = primary;
        buttons.Children.Add(btnCancel);
        buttons.Children.Add(btnAdd);
        root.Children.Add(buttons);

        win.Content = root;

        ExternalSlot? result = null;

        btnCancel.Click += (_, _) => win.Close();
        btnAdd.Click += async (_, _) =>
        {
            string address = txtAddress.Text.Trim();
            string slot = txtSlot.Text.Trim();
            string password = txtPassword.Text;

            if (address.Length == 0 || slot.Length == 0)
            {
                status.Foreground = Brush("BrushError", "#D9534F");
                status.Text = "Both the server address and the slot name are needed.";
                return;
            }

            btnAdd.IsEnabled = false;
            btnAdd.Content = "Checking…";
            status.Foreground = Brush("BrushAccent", "#E0A82E");
            status.Text = $"Asking {address} about \"{slot}\"…";

            ApSlotProbeResult probe;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                probe = await ApSlotProbe.ResolveGameAsync(address, slot, password, cts.Token);
            }
            catch (Exception ex)
            {
                probe = new ApSlotProbeResult(null, null, ex.Message.Split('\n')[0]);
            }

            btnAdd.IsEnabled = true;
            btnAdd.Content = "Check and add";

            if (probe.Game == null)
            {
                // Say which of the two things went wrong, because they need
                // different fixes: a refusal means the name or password is
                // wrong, a transport error means the address is.
                status.Foreground = Brush("BrushError", "#D9534F");
                status.Text = probe.Refusal is { Length: > 0 }
                    ? "The server refused that slot: " + string.Join(", ", probe.Refusal)
                      + ". Check the name and password."
                    : "Could not reach that server" +
                      (probe.Error is { Length: > 0 } ? $" ({probe.Error})" : "") +
                      ". Check the address and that the host has it running.";
                return;
            }

            result = new ExternalSlot(
                Id: Guid.NewGuid().ToString("N")[..12],
                Address: address,
                SlotName: slot,
                Password: password,
                Game: probe.Game,
                Added: DateTime.Now);
            ExternalSlotStore.Add(result);
            win.Close();
        };

        win.ShowDialog();
        return result;
    }

    private static TextBox Field(Panel host, string label, string placeholder, string hint)
    {
        host.Children.Add(new TextBlock
        {
            Text = label.ToUpperInvariant(),
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 10, 0, 4),
            Foreground = Brush("BrushMuted", "#8A93AD"),
        });
        var box = new TextBox
        {
            Text = placeholder,
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 13,
        };
        host.Children.Add(box);
        host.Children.Add(new TextBlock
        {
            Text = hint,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = Brush("BrushMuted", "#8A93AD"),
            Opacity = 0.8,
        });
        return box;
    }

    /// Themed brush when the app's resources are loaded, a literal otherwise —
    /// this dialog must also work from a context that has no App resources.
    private static Brush Brush(string key, string fallback)
    {
        try
        {
            if (Application.Current?.TryFindResource(key) is Brush b) return b;
        }
        catch (Exception) { }
        return (Brush)new BrushConverter().ConvertFrom(fallback)!;
    }
}
