using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace LauncherV2.Core.Plugins;

// The consent moment: who wrote it, what it declares, the file hash.
// Approve is disabled for a few seconds so reading is the default.

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

    /// Seconds the approve button stays disabled.
    private const int ReadDelaySeconds = 3;

    private readonly Button _approve;
    private readonly DispatcherTimer _timer;
    private int _remaining = ReadDelaySeconds;
    private bool _approved;

    ///
    /// Ask the player. Returns true when they approved; the caller then
    /// installs and records the hash. Returns false on cancel or close.
    ///
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

        // An update and a first install are the same dialog, but not the same
        // sentence -- the title is set from the trust file below.
        Title = PluginTrustStore.Get(m.GameId) != null ? "Update plugin" : "Add plugin";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = WindowBg;

        var root = new StackPanel { Margin = new Thickness(22) };

        root.Children.Add(Text($"{m.DisplayName}  {m.Version}", 18, FontWeights.Bold, Fg));
        root.Children.Add(Text("by " + m.Author, 12, FontWeights.Normal, Muted,
                               new Thickness(0, 2, 0, 14)));

        // Hvem har lavet hvad. ⚠ Slaas op i LAUNCHERENS egen liste, aldrig i
        // pakken — ellers kunne enhver skrive sig selv paa som betroet.
        var prov = FirstParty.For(m.GameId);
        var accent = prov.NeedsExplicitConfirmation ? Warn : Muted;

        var box = new Border
        {
            Background = PanelBg, BorderBrush = accent, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 14),
        };
        var inner = new StackPanel();
        inner.Children.Add(Text(prov.Headline, 13, FontWeights.SemiBold, accent));
        inner.Children.Add(Text(prov.Body, 12, FontWeights.Normal, Fg,
                                new Thickness(0, 8, 0, 0)));

        // Spillet er en andens: vis hvor det kommer fra, saa spilleren kan se
        // efter selv i stedet for at tage vores ord for det.
        if (!prov.IsFirstPartyGame && prov.GameUrl is { Length: > 0 })
            inner.Children.Add(Text($"{prov.GameName}   {prov.GameUrl}",
                                    11, FontWeights.Normal, Muted,
                                    new Thickness(0, 8, 0, 0)));

        // Kun fremmed kode faar sikkerhedsafsnittet. Staar det paa hver eneste
        // dialog, holder folk op med at laese det — og saa virker det ikke
        // den ene gang det betyder noget.
        if (!prov.IsFirstPartyPlugin)
            inner.Children.Add(Text(
                "It runs as a normal program on your computer, with your rights. "
              + "It can read and write files and use the internet.",
                12, FontWeights.Normal, Fg, new Thickness(0, 8, 0, 0)));

        // Approving a plugin that updates itself includes those future
        // updates — say so here, where the approval happens, so the automatic
        // update that later lands is something the player agreed to rather
        // than something that merely happened. Same test as the behaviour
        // (PluginInstallFlow), so the sentence and the fact cannot drift.
        if (PluginAutoUpdatePolicy.WouldAutoUpdate(m))
            inner.Children.Add(Text(
                "Updates to this plugin are published by the launcher's own "
              + "developer and install automatically, verified against their "
              + "published checksum.",
                12, FontWeights.Normal, Fg, new Thickness(0, 8, 0, 0)));

        box.Child = inner;
        root.Children.Add(box);

        // Replacing something already approved? Say so, and say what it was.
        //
        // Without this, an update looks exactly like a first install, and the
        // player has no way to notice that the version they trusted is being
        // swapped for one they have not seen.
        var previous = PluginTrustStore.Get(m.GameId);
        IReadOnlyList<string> before = Array.Empty<string>();
        if (previous != null)
        {
            before = PreviousDeclarations(m.GameId);
            string when = DateTime.TryParse(previous.Approved, out var d)
                ? d.ToString("d MMM yyyy") : "earlier";
            root.Children.Add(Text(
                $"This replaces version {previous.Version}, which you approved {when}.",
                12, FontWeights.Normal, Muted, new Thickness(0, 0, 0, 12)));
        }

        // What it says about itself — the manifest, read without running code.
        var declared = m.Declares.Describe(m.GameId);
        if (declared.Count > 0)
        {
            root.Children.Add(Text("The plugin states that it:", 12, FontWeights.SemiBold, Fg));
            foreach (string line in declared)
            {
                // A line the previous version did NOT claim is the whole reason
                // to read this dialog a second time. An update that quietly
                // starts downloading from somewhere new must not look identical
                // to one that only fixed a bug.
                bool isNew = previous != null
                          && !before.Contains(line, StringComparer.OrdinalIgnoreCase);
                root.Children.Add(Text(
                            "   •  " + line + (isNew ? "     (new in this version)" : ""),
                    12, isNew ? FontWeights.SemiBold : FontWeights.Normal,
                    isNew ? Warn : Fg, new Thickness(0, 3, 0, 0)));
            }
            root.Children.Add(new Border { Height = 12 });
        }

        root.Children.Add(Text("SHA-256   " + c.ShortHash, 11, FontWeights.Normal, Muted));
        if (!string.IsNullOrWhiteSpace(m.AuthorContact))
            root.Children.Add(Text("Contact   " + m.AuthorContact, 11, FontWeights.Normal, Muted,
                                   new Thickness(0, 2, 0, 0)));

        if (!prov.IsFirstPartyPlugin)
            root.Children.Add(Text(
                "Responsibility for what this plugin does lies with its author, "
              + "not with Multiworld Launcher.",
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

        string verb = previous != null ? "update" : "add";
        _approve = MakeButton($"I understand — {verb}  ({_remaining})", isDefault: false);
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
                _approve.Content = $"I understand — {verb}  ({_remaining})";
                return;
            }
            _timer.Stop();
            _approve.Content = $"I understand — {verb}";
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

    ///
    /// What the currently installed copy claims, read off disk.
    ///
    /// Read from the INSTALLED folder rather than the trust file, which stores
    /// only a version and a hash. This runs before anything is unpacked -- the
    /// old plugin.json is still there -- so the comparison is against what the
    /// player actually has, not against what we remember about it.
    ///
    private static IReadOnlyList<string> PreviousDeclarations(string gameId)
    {
        try
        {
            string path = System.IO.Path.Combine(
                PluginPackage.DirectoryFor(gameId), PluginManifest.FileName);
            if (!System.IO.File.Exists(path)) return Array.Empty<string>();

            var old = PluginManifest.Parse(System.IO.File.ReadAllText(path), out _);
            return old?.Declares.Describe(gameId) ?? Array.Empty<string>();
        }
        catch
        {
            // Unreadable: fall back to marking nothing as new. Wrongly claiming
            // a line is new is noise; wrongly claiming one is old is not, so
            // this direction is the safe one -- the full list is still shown.
            return Array.Empty<string>();
        }
    }

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
