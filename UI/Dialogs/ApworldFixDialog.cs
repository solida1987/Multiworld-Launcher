using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core.Archipelago;

namespace LauncherV2.UI.Dialogs;

///
/// "We found something wrong with a world you have. Want us to fix it?"
///
/// ⚠ These are other people's worlds. London did not write them and is not
/// responsible for them, so this window exists to INFORM and OFFER — never to
/// act. It says what is wrong, what it costs, and exactly what London would
/// do; the player ticks what they want fixed, or closes it and nothing
/// happens. A no is remembered for that exact file so the question is asked
/// once, not every start-up.
///
public sealed class ApworldFixDialog : Window
{
    private static readonly Brush Ink   = new SolidColorBrush(Color.FromRgb(0xE6, 0xEA, 0xF2));
    private static readonly Brush Dim   = new SolidColorBrush(Color.FromRgb(0x8A, 0x93, 0xA8));
    private static readonly Brush Warn  = new SolidColorBrush(Color.FromRgb(0xE0, 0x9A, 0x3C));

    private readonly List<(ApworldDoctor.Issue Issue, CheckBox Box)> _rows = new();

    /// The issues the player agreed to fix. Empty when they said no.
    public IReadOnlyList<ApworldDoctor.Issue> Accepted { get; private set; } =
        Array.Empty<ApworldDoctor.Issue>();

    /// Everything they were shown, so a decline can be remembered per file.
    public IReadOnlyList<ApworldDoctor.Issue> Offered { get; }

    private ApworldFixDialog(Window? owner, IReadOnlyList<ApworldDoctor.Issue> issues)
    {
        Offered = issues;
        Owner = owner;
        Title = "A world in your Archipelago install cannot load";
        Width = 720;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 700;
        WindowStartupLocation = owner is null
            ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x11, 0x14, 0x1C));
        ResizeMode = ResizeMode.NoResize;

        var root = new StackPanel { Margin = new Thickness(22) };

        root.Children.Add(new TextBlock
        {
            Text = issues.Count == 1
                ? "One of your AP worlds has a file name Archipelago cannot load"
                : $"{issues.Count} of your AP worlds have file names Archipelago cannot load",
            Foreground = Ink, FontSize = 16, FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
        });

        // Whose problem this is, said plainly. London did not make these and
        // will not touch them uninvited.
        root.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 16),
            Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Text = "These are worlds other people wrote, and London does not change them "
                 + "on its own. It can rename the file for you — nothing inside the world "
                 + "is touched — or you can leave it and sort it out yourself. If you say "
                 + "no, you will not be asked about these files again.",
        });

        foreach (var issue in issues)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x16, 0x1A, 0x26)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x2B, 0x3D)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 0, 10),
            };
            var body = new StackPanel();

            var box = new CheckBox
            {
                IsChecked = true,
                Foreground = Ink,
                FontWeight = FontWeights.SemiBold,
                Content = issue.FileName,
            };
            body.Children.Add(box);
            _rows.Add((issue, box));

            body.Children.Add(Line("What is wrong", issue.Problem, Dim));
            body.Children.Add(Line("What it costs you", issue.Consequence, Warn));
            body.Children.Add(Line("What London would do", issue.Fix, Dim));

            card.Child = body;
            root.Children.Add(card);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0),
        };
        var no = new Button { Content = "No, leave them alone", Padding = new Thickness(14, 6, 14, 6),
                              Margin = new Thickness(0, 0, 8, 0) };
        no.Click += (_, _) => { Accepted = Array.Empty<ApworldDoctor.Issue>(); DialogResult = false; };
        var yes = new Button { Content = "Fix the ticked ones", Padding = new Thickness(14, 6, 14, 6),
                               IsDefault = true };
        yes.Click += (_, _) =>
        {
            Accepted = _rows.Where(r => r.Box.IsChecked == true)
                            .Select(r => r.Issue).ToList();
            DialogResult = true;
        };
        buttons.Children.Add(no);
        buttons.Children.Add(yes);
        root.Children.Add(buttons);

        Content = new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    private static UIElement Line(string label, string text, Brush colour)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        sp.Children.Add(new TextBlock
        {
            Text = label.ToUpperInvariant(), Foreground = Dim, FontSize = 10,
            FontWeight = FontWeights.Bold,
        });
        sp.Children.Add(new TextBlock
        {
            Text = text, Foreground = colour, FontSize = 12,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
        });
        return sp;
    }

    /// Show the offer. Returns what the player agreed to, and what they were
    /// shown, so both halves of the answer can be recorded.
    public static (IReadOnlyList<ApworldDoctor.Issue> Accepted,
                   IReadOnlyList<ApworldDoctor.Issue> Offered)
        Ask(Window? owner, IReadOnlyList<ApworldDoctor.Issue> issues)
    {
        if (issues.Count == 0)
            return (Array.Empty<ApworldDoctor.Issue>(), Array.Empty<ApworldDoctor.Issue>());
        var dlg = new ApworldFixDialog(owner, issues);
        dlg.ShowDialog();
        return (dlg.Accepted, dlg.Offered);
    }
}
