using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace LauncherV2.Core.Plugins;

// The one moment where the player decides whether to run somebody else's code.
//
// Everything shown here was read out of the package without executing any of
// it: the manifest, and a hash of the exact bytes. That is what makes the
// dialog worth showing at all — by the time it appears, nothing from the
// plugin has run, and pressing Cancel leaves nothing behind.
//
// The approve button is disabled for a moment on purpose. It is not a delay
// for its own sake: a dialog that can be dismissed by the reflex that opened
// the file picker is a dialog nobody reads, and then the whole consent model is
// decoration. Built in code like the rest of the launcher's dialogs.

internal sealed class PluginConsentDialog : Window
{
    private static readonly Brush Fg      = Frozen(0xCC, 0xD0, 0xE0);
    private static readonly Brush Muted   = Frozen(0x72, 0x7A, 0x99);
    private static readonly Brush PanelBg = Frozen(0x10, 0x14, 0x22);
    private static readonly Brush WindowBg= Frozen(0x0A, 0x0D, 0x18);
    private static readonly Brush BorderBr= Frozen(0x2A, 0x30, 0x50);
    private static readonly Brush Warn    = Frozen(0xF5, 0x9E, 0x0B);
    private static readonly Brush BtnBg   = Frozen(0x1A, 0x1E, 0x30);

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    /// <summary>Seconds the approve button stays disabled.</summary>
    private const int ReadDelaySeconds = 3;

    private readonly Button _approve;
    private readonly DispatcherTimer _timer;
    private int _remaining = ReadDelaySeconds;
    private bool _approved;

    /// <summary>
    /// Ask the player. Returns true when they approved; the caller then
    /// installs and records the hash. Returns false on cancel or close.
    /// </summary>
    public static bool Ask(Window? owner, PluginCandidate candidate)
    {
        if (!candidate.IsUsable) return false;
        var dlg = new PluginConsentDialog(candidate) { Owner = owner };
        dlg.ShowDialog();
        return dlg._approved;
    }

    private PluginConsentDialog(PluginCandidate c)
    {
        var m = c.Manifest!;

        Title = "Add plugin";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = WindowBg;

        var root = new StackPanel { Margin = new Thickness(22) };

        root.Children.Add(Text($"{m.DisplayName}  {m.Version}", 18, FontWeights.Bold, Fg));
        root.Children.Add(Text("by " + m.Author, 12, FontWeights.Normal, Muted,
                               new Thickness(0, 2, 0, 14)));

        // The warning, not buried under the details.
        var warnBox = new Border
        {
            Background = PanelBg, BorderBrush = Warn, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 14),
        };
        var warn = new StackPanel();
        warn.Children.Add(Text(
            "This plugin was not built by solida1987 and has not been reviewed "
          + "by Multiworld Launcher.", 13, FontWeights.SemiBold, Warn));
        warn.Children.Add(Text(
            "It runs as a normal program on your computer, with your rights. It "
          + "can read and write files and use the internet. Only add plugins "
          + "from someone you trust.", 12, FontWeights.Normal, Fg,
            new Thickness(0, 8, 0, 0)));
        warnBox.Child = warn;
        root.Children.Add(warnBox);

        // What it says about itself — the manifest, read without running code.
        var declared = m.Declares.Describe(m.GameId);
        if (declared.Count > 0)
        {
            root.Children.Add(Text("The plugin states that it:", 12, FontWeights.SemiBold, Fg));
            foreach (string line in declared)
                root.Children.Add(Text("   •  " + line, 12, FontWeights.Normal, Fg,
                                       new Thickness(0, 3, 0, 0)));
            root.Children.Add(new Border { Height = 12 });
        }

        root.Children.Add(Text("SHA-256   " + c.ShortHash, 11, FontWeights.Normal, Muted));
        if (!string.IsNullOrWhiteSpace(m.AuthorContact))
            root.Children.Add(Text("Contact   " + m.AuthorContact, 11, FontWeights.Normal, Muted,
                                   new Thickness(0, 2, 0, 0)));

        root.Children.Add(Text(
            "Responsibility for this game following Archipelago's rules lies "
          + "with the plugin's author, not with Multiworld Launcher.",
            11, FontWeights.Normal, Muted, new Thickness(0, 14, 0, 0)));

        // Buttons. Cancel is the default action, so Esc and Enter both decline.
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };
        var cancel = MakeButton("Cancel", isDefault: true);
        cancel.Click += (_, _) => Close();

        _approve = MakeButton($"I understand — add  ({_remaining})", isDefault: false);
        _approve.IsEnabled = false;
        _approve.Margin = new Thickness(10, 0, 0, 0);
        _approve.Click += (_, _) => { _approved = true; Close(); };

        row.Children.Add(cancel);
        row.Children.Add(_approve);
        root.Children.Add(row);

        Content = root;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            _remaining--;
            if (_remaining > 0)
            {
                _approve.Content = $"I understand — add  ({_remaining})";
                return;
            }
            _timer.Stop();
            _approve.Content = "I understand — add";
            _approve.IsEnabled = true;
        };
        _timer.Start();

        Closed += (_, _) => _timer.Stop();
    }

    private static TextBlock Text(string s, double size, FontWeight weight, Brush brush,
                                  Thickness? margin = null)
        => new()
        {
            Text = s, FontSize = size, FontWeight = weight, Foreground = brush,
            TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0),
        };

    private static Button MakeButton(string content, bool isDefault) => new()
    {
        Content = content,
        Padding = new Thickness(18, 8, 18, 8),
        Background = BtnBg,
        Foreground = Fg,
        BorderBrush = BorderBr,
        BorderThickness = new Thickness(1),
        IsDefault = isDefault,
        IsCancel = isDefault,
        Cursor = System.Windows.Input.Cursors.Hand,
    };
}
