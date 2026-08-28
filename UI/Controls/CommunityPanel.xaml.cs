using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LauncherV2.Core;

namespace LauncherV2.UI.Controls;

// CommunityPanel — the two places outside London where Archipelago happens.
//
// Streamers and Discord servers are different enough to warrant two buttons in
// the topbar and similar enough to share one control: both are a list of cards
// about somebody else, and both live or die on the same rule -- if we cannot
// confirm something, the card does not say it.
//
// So there are no invented descriptions here. Discord's own endpoint supplies
// the name, the icon and the member count; a server whose invite has since been
// revoked simply shows no numbers instead of stale ones. The streamer figures
// carry the date they were read, printed next to them.
public partial class CommunityPanel : System.Windows.Controls.UserControl
{
    public enum Face { Streamers, Discord }

    private Face _face = Face.Streamers;
    private bool _loaded;

    public CommunityPanel()
    {
        InitializeComponent();
    }

    /// Called by the topbar. Cheap to call repeatedly — the fetch happens once.
    public void Show(Face face) => _ = ShowAsync(face);

    /// The same work, awaitable.
    ///
    /// Show() drops the task on the floor, which is right for a button click
    /// and wrong for anything checking this panel: a throw inside a dropped
    /// task faults it silently, and a harness that only looks at the window
    /// afterwards sees a half-drawn page and calls it fine. That is exactly
    /// how an invented style key reached a release — the render proof passed
    /// while the panel was throwing. Anything verifying this awaits here.
    public async Task ShowAsync(Face face)
    {
        _face = face;
        ApplyChrome();
        if (!_loaded) { _loaded = true; await LoadAsync(); }
        else await RenderAsync();
    }

    private void ApplyChrome()
    {
        if (_face == Face.Streamers)
        {
            TxtHeading.Text = "People who stream Archipelago";
            TxtSubheading.Text =
                "Watching somebody else play a multiworld is the fastest way to "
                + "understand what one actually feels like — the waiting, the moment "
                + "somebody else's game hands you the thing you were stuck on. These "
                + "are channels that have run Archipelago sessions, in several "
                + "languages.";
            TxtFooter.Text =
                "Follower and subscriber figures are shown with the date they were "
                + "checked, because nobody can ask Twitch or YouTube for them without "
                + "an API key. Treat an old date as an old number.";
        }
        else
        {
            TxtHeading.Text = "Archipelago communities on Discord";
            TxtSubheading.Text =
                "Where runs get organised, where people help each other through a "
                + "configuration that will not cooperate, and where a bug in a game "
                + "reaches the person who maintains it. Several languages — "
                + "Archipelago is not an English-only community.";
            TxtFooter.Text =
                "Names, icons and member counts are asked of Discord as this page "
                + "opens, so they are current. London is not affiliated with any of "
                + "these servers and does not moderate them.";
        }
    }

    private async Task LoadAsync()
    {
        TxtEmpty.Text = "Loading…";
        await RenderAsync();
    }

    private async Task RenderAsync()
    {
        var dir = await CommunityDirectory.LoadAsync();
        PanelCards.Children.Clear();

        if (_face == Face.Streamers)
        {
            var list = dir.Streamers ?? new List<CommunityDirectory.Streamer>();
            TxtEmpty.Text = list.Count == 0
                ? "Nothing is listed here yet. Rather than fill the page with names "
                + "we have not checked, it stays empty until there is something "
                + "accurate to put on it."
                : "";
            foreach (var s in list) PanelCards.Children.Add(BuildStreamerCard(s));
            return;
        }

        var servers = dir.Discord ?? new List<CommunityDirectory.DiscordServer>();
        TxtEmpty.Text = servers.Count == 0
            ? "Nothing is listed here yet."
            : "";

        // Build every card first, then let each fill in its own live numbers.
        // One slow or revoked invite must not hold up the other five.
        foreach (var server in servers)
        {
            var (card, fill) = BuildDiscordCard(server);
            PanelCards.Children.Add(card);
            _ = fill();
        }
    }

    // ------------------------------------------------------------- streamers

    private UIElement BuildStreamerCard(CommunityDirectory.Streamer s)
    {
        var card = NewCard(width: 372);
        var stack = new StackPanel();

        var head = new StackPanel { Orientation = Orientation.Horizontal };

        // A real avatar when the list carries an address for one. The catalogue
        // stores the ADDRESS, never the picture -- the same rule the cover art
        // follows, and for the same reason: it is not ours.
        head.Children.Add(Avatar(s.ImageUrl, s.Name));

        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock
        {
            Text = s.Name,
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("BrushText"),
        });
        if (s.Language is { Length: > 0 })
            titles.Children.Add(new TextBlock
            {
                Text = s.Language,
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = (Brush)FindResource("BrushMuted"),
            });
        head.Children.Add(titles);
        stack.Children.Add(head);

        stack.Children.Add(new TextBlock
        {
            Text = s.Blurb,
            FontSize = 12,
            LineHeight = 18,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
            Foreground = (Brush)FindResource("BrushMuted"),
        });

        // Figures, each labelled with where it comes from, and the whole line
        // dated. An undated audience number is a claim; a dated one is a fact
        // about a day.
        var figures = new List<string>();
        if (s.Followers is { Length: > 0 })   figures.Add($"{s.Followers} followers on Twitch");
        if (s.Subscribers is { Length: > 0 }) figures.Add($"{s.Subscribers} subscribers on YouTube");
        if (figures.Count > 0)
            stack.Children.Add(new TextBlock
            {
                Text = string.Join("   ·   ", figures)
                     + (s.Checked is { Length: > 0 } ? $"\nas of {s.Checked}" : ""),
                FontSize = 11,
                LineHeight = 16,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 11, 0, 0),
                Foreground = (Brush)FindResource("BrushText"),
            });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 13, 0, 0),
        };
        if (s.Twitch is { Length: > 0 } tw)
            buttons.Children.Add(LinkButton("Watch on Twitch", tw, true));
        if (s.YouTube is { Length: > 0 } yt)
            buttons.Children.Add(LinkButton("YouTube", yt, false));

        card.Child = Compose(stack, buttons);
        return card;
    }

    /// Content on top, actions pinned to the bottom of the card.
    ///
    /// A WrapPanel stretches every card in a row to the tallest one, so with a
    /// plain stack the buttons ended up at whatever height each card's text
    /// happened to finish — three cards, three button heights, in a row that
    /// was otherwise perfectly aligned.
    private static Grid Compose(UIElement content, UIElement footer)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(content, 0);
        Grid.SetRow(footer, 1);
        grid.Children.Add(content);
        grid.Children.Add(footer);
        return grid;
    }

    // --------------------------------------------------------------- discord

    /// Returns the card and the job that fills in its live half, so the caller
    /// can start them all and let each finish on its own.
    private (Border Card, Func<Task> Fill) BuildDiscordCard(
        CommunityDirectory.DiscordServer server)
    {
        var card = NewCard(width: 372);
        var stack = new StackPanel();

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = Avatar(null, server.Blurb);       // replaced once Discord answers
        head.Children.Add(icon);

        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock
        {
            Text = "…",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("BrushText"),
        };
        titles.Children.Add(name);

        var sub = new StackPanel { Orientation = Orientation.Horizontal,
                                   Margin = new Thickness(0, 3, 0, 0) };
        if (server.Official)
            sub.Children.Add(Chip("OFFICIAL", (Brush)FindResource("BrushAccent")));
        if (server.Language is { Length: > 0 })
            sub.Children.Add(Chip(server.Language, (Brush)FindResource("BrushMuted")));
        titles.Children.Add(sub);
        head.Children.Add(titles);
        stack.Children.Add(head);

        var counts = new TextBlock
        {
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 11, 0, 0),
            Foreground = (Brush)FindResource("BrushSuccess"),
            Visibility = Visibility.Collapsed,
        };
        stack.Children.Add(counts);

        stack.Children.Add(new TextBlock
        {
            Text = server.Blurb,
            FontSize = 12,
            LineHeight = 18,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = (Brush)FindResource("BrushMuted"),
        });

        if (server.Note is { Length: > 0 })
            stack.Children.Add(new TextBlock
            {
                Text = server.Note,
                FontSize = 11,
                FontStyle = FontStyles.Italic,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = (Brush)FindResource("BrushText"),
            });

        var open = LinkButton("Open the invite",
                              CommunityDirectory.InviteUrl(server.InviteCode), true);
        open.Margin = new Thickness(0, 13, 0, 0);

        card.Child = Compose(stack, open);

        async Task Fill()
        {
            var live = await CommunityDirectory.FetchLiveAsync(server.InviteCode);
            if (live == null)
            {
                // Revoked, or no network. Fall back to the invite code as the
                // only name we can stand behind, and show no numbers at all.
                name.Text = "Discord server";
                return;
            }

            name.Text = live.Name ?? "Discord server";
            if (live.Members > 0)
            {
                counts.Text = $"{live.Members:N0} members   ·   {live.Online:N0} online now";
                counts.Visibility = Visibility.Visible;
            }
            if (live.IconUrl is { Length: > 0 } url) SetAvatar(icon, url);
        }

        return (card, Fill);
    }

    // ----------------------------------------------------------------- parts

    private Border NewCard(double width) => new()
    {
        Width = width,
        Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x20, 0x30)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x2C, 0x3E)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(17, 15, 17, 16),
        Margin = new Thickness(0, 0, 14, 14),
    };

    /// A round 52 px avatar. With no picture it shows the first letter, which
    /// is a deliberate design and not a placeholder waiting to be replaced.
    private Border Avatar(string? url, string fallbackText)
    {
        var host = new Border
        {
            Width = 52,
            Height = 52,
            CornerRadius = new CornerRadius(26),
            Background = new SolidColorBrush(Color.FromRgb(0x26, 0x2C, 0x3E)),
            Margin = new Thickness(0, 0, 13, 0),
            VerticalAlignment = VerticalAlignment.Top,
            ClipToBounds = true,
            Child = new TextBlock
            {
                Text = fallbackText.Length > 0
                    ? fallbackText.Substring(0, 1).ToUpperInvariant() : "•",
                FontSize = 21,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("BrushMuted"),
            },
        };
        if (url is { Length: > 0 }) SetAvatar(host, url);
        return host;
    }

    private static void SetAvatar(Border host, string url)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(url, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            // No DelayCreation here. It defers the fetch until something first
            // paints the image, which is too late for anything that renders the
            // page in one pass -- every avatar came out an empty circle. EndInit
            // starts the download now; the letter underneath stands until it
            // lands, which is the behaviour the fallback was written for.
            bmp.EndInit();

            // Painted as the BACKGROUND, not as a child. A Border's
            // CornerRadius does not clip what is inside it -- ClipToBounds does
            // not either -- so an Image child came out square in a round hole.
            // The background brush is drawn into the rounded geometry, which is
            // what makes the circle real.
            host.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            host.Child = null;
        }
        catch (Exception)
        {
            // A picture that will not load is not worth losing the card over.
        }
    }

    private Border Chip(string text, Brush colour) => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x26, 0x2C, 0x3E)),
        CornerRadius = new CornerRadius(3),
        Padding = new Thickness(6, 1, 6, 2),
        Margin = new Thickness(0, 0, 6, 0),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 9.5,
            FontWeight = FontWeights.Bold,
            Foreground = colour,
        },
    };

    private Button LinkButton(string text, string url, bool primary)
    {
        var b = new Button
        {
            Content = text,
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 8, 0),
            Style = (Style)FindResource(primary ? "BtnPlayStyle" : "BtnSecondaryStyle"),
        };
        b.Click += (_, _) => OpenUrl(url);
        return b;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception) { }
    }
}
