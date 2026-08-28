using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core;
using LauncherV2.Core.Archipelago;
using LauncherV2.Core.Plugins;
using LauncherV2.UI.Dialogs;

namespace LauncherV2.UI.Controls;

// The other half of Join: slots on servers we are not running.
//
// A seed in your own library carries a manifest, so the tab can list its slots
// without asking anybody. A session somebody else hosts carries nothing here —
// you were handed an address and a name, and that is all. So these slots are
// signed in to one at a time and remembered, and each card then answers the
// same question the seed cards answer: can this machine play it, and if not,
// exactly what is missing.
//
// One difference matters. Joining does not need the Archipelago engine at all:
// nothing is generated and nothing is hosted, so the engine check that guards
// the seed cards would refuse a slot that works perfectly well.
public partial class JoinPanel
{
    private void BtnJoinElsewhere_Click(object sender, RoutedEventArgs e)
    {
        if (ExternalSlotDialog.Show(Window.GetWindow(this)) != null)
            RefreshCards();
    }

    private void RefreshExternalCards()
    {
        PanelExternal.Children.Clear();
        var slots = ExternalSlotStore.All();

        bool any = slots.Count > 0;
        SectionExternal.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (!any) return;

        TxtExternalCount.Text = slots.Count == 1
            ? "1 slot" : $"{slots.Count} slots";

        foreach (var slot in slots.OrderBy(s => s.Address).ThenBy(s => s.SlotName))
            PanelExternal.Children.Add(BuildExternalCard(slot));
    }

    private UIElement BuildExternalCard(ExternalSlot ext)
    {
        var plugin = ext.Game == null ? null : GameRegistry.All.FirstOrDefault(p =>
            string.Equals(p.ApWorldName, ext.Game, StringComparison.OrdinalIgnoreCase));
        var session = plugin == null ? null : ApJoinSession.For(plugin);
        bool playing = session is { Current: ApJoinSession.Stage.Playing
                                           or ApJoinSession.Stage.Connecting };

        var card = new Border
        {
            Width = 330,
            Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x20, 0x30)),
            BorderBrush = new SolidColorBrush(playing
                ? Color.FromRgb(0x4F, 0xA9, 0x7B) : Color.FromRgb(0x26, 0x2C, 0x3E)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(15, 12, 15, 13),
            Margin = new Thickness(0, 0, 12, 12),
        };
        var stack = new StackPanel();

        // Slot name first, address under it — you log in as the name, and the
        // address is how you tell two sessions apart when you hold slots in both.
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x26, 0x2C, 0x3E)),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 0, 8, 0),
            Child = new TextBlock
            {
                Text = ext.SlotName,
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("BrushMuted"),
            },
        });
        stack.Children.Add(head);

        stack.Children.Add(new TextBlock
        {
            Text = ext.Game ?? "Unknown game",
            FontSize = 14.5,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 7, 0, 1),
            Foreground = (Brush)FindResource("BrushText"),
        });
        stack.Children.Add(new TextBlock
        {
            Text = ext.DisplayAddress,
            FontSize = 10.5,
            Margin = new Thickness(0, 0, 0, 5),
            Foreground = (Brush)FindResource("BrushMuted"),
        });

        var status = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 9),
            Foreground = (Brush)FindResource("BrushMuted"),
        };
        stack.Children.Add(status);

        Button Btn(string text, bool primary)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(0, 8, 0, 8),
                Style = (Style)FindResource(primary ? "BtnPlayStyle" : "BtnSecondaryStyle"),
            };
            stack.Children.Add(b);
            return b;
        }

        if (playing)
        {
            status.Text = session!.StatusText;
            stack.Children.Add(new TextBlock
            {
                Text = $"{session.ChecksSent} checks sent   ·   {session.ItemsReceived} items received",
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 9),
                Foreground = (Brush)FindResource("BrushSuccess"),
            });
            var stop = Btn("Stop", primary: false);
            stop.Click += async (_, _) => { await session.StopAsync(); RefreshCards(); };
        }
        else if (plugin == null)
        {
            // Nothing installed covers this game. The catalogue decides which of
            // the two honest answers this card gives -- the same two the seed
            // cards give, from the same map.
            string key = ext.Game ?? "";
            if (_catalogue == null)
                status.Text = "Checking whether London has a plugin for this…";
            else if (_catalogue.TryGetValue(key, out var entry))
            {
                status.Text = $"You are signed in as \"{ext.SlotName}\" and the server "
                            + $"says this slot plays {key}. London has a plugin for it — "
                            + "install it and the slot becomes playable.";
                var b = Btn("Install plugin", primary: true);
                b.Click += async (_, _) => await InstallPluginAsync(entry, b, status);
            }
            else
                status.Text = key.Length == 0
                    ? "The server never said which game this slot plays. Remove the "
                    + "slot and add it again once the session is up."
                    : $"No London plugin covers {key} yet. The slot still works from "
                    + "that game's own Archipelago client.";
        }
        else
        {
            var missing = MissingForPlay(plugin);
            if (missing.Count == 0)
            {
                status.Text = "Everything is in place — press Play and London connects "
                            + "as this slot and starts the game.";
                var b = Btn("▶  Play this slot", primary: true);
                b.Click += async (_, _) => await PlayExternalAsync(ext, plugin, b, status);
            }
            else
            {
                // Say all of it at once. Fixing one thing and being told about
                // the next is the same walk three times.
                status.Text = "Before this slot can start:\n• " + string.Join("\n• ", missing);
                var b = Btn("Open " + plugin.DisplayName, primary: false);
                b.Click += (_, _) => OpenGameRequested?.Invoke(plugin);
            }
        }

        var forget = new TextBlock
        {
            Text = "Forget this slot",
            FontSize = 10,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = (Brush)FindResource("BrushMuted"),
            Cursor = System.Windows.Input.Cursors.Hand,
            TextDecorations = TextDecorations.Underline,
        };
        forget.MouseLeftButtonUp += (_, _) =>
        {
            ExternalSlotStore.Remove(ext.Id);
            RefreshCards();
        };
        stack.Children.Add(forget);

        card.Child = stack;
        return card;
    }

    /// Everything standing between this plugin and a working session, in the
    /// order a player would fix it. Empty means ready.
    ///
    /// Deliberately not the engine: joining somebody else's server neither
    /// generates nor hosts, so an engine that is missing changes nothing here.
    private static List<string> MissingForPlay(IGamePlugin plugin)
    {
        var missing = new List<string>();

        if (!plugin.IsInstalled)
            missing.Add($"{plugin.DisplayName} is not installed yet — open it in the "
                      + "library and install it.");

        if (!plugin.HasBaseGameFiles())
            missing.Add("London needs pointing at your own copy of the game.");

        if (plugin.UsesRomLibrary)
        {
            try
            {
                if (plugin.GetUnmetRomRequirement() is { } req)
                    // Name the exact dump. "A ROM is needed" sends someone back
                    // to a search engine; the version label ends the question --
                    // and a ROM that is present but wrong is a different problem
                    // from no ROM at all, so it gets different words.
                    missing.Add(req.WrongVersionPresent
                        ? $"A {req.SystemLabel} file is set, but not the right one. "
                        + $"This needs {req.VersionLabel}."
                        : $"Your own {req.VersionLabel} ({req.SystemLabel}). London "
                        + "patches a copy for this slot and never touches the original.");
            }
            catch (Exception)
            {
                // A plugin that throws while describing its own requirement is
                // not a reason to hide the card; the launch path asks again.
            }
        }

        return missing;
    }

    private async Task PlayExternalAsync(ExternalSlot ext, IGamePlugin plugin,
                                         Button button, TextBlock status)
    {
        button.IsEnabled = false;
        button.Content = "Connecting…";
        status.Foreground = (Brush)FindResource("BrushAccent");
        status.Text = $"Signing in to {ext.DisplayAddress} as \"{ext.SlotName}\"…";

        var (session, message) = await ApJoinSession.StartExternalAsync(ext, plugin);

        if (session == null)
        {
            status.Foreground = (Brush)FindResource("BrushError");
            status.Text = message;
            button.Content = "▶  Play this slot";
            button.IsEnabled = true;
            return;
        }
        RefreshCards();
    }

    /// Asked when a card cannot start and the fix lives in the game's own page.
    public event Action<IGamePlugin>? OpenGameRequested;
}
