using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using LauncherV2.Core.Extensions;

namespace LauncherV2.Core.Emulators;

/// The moment the player decides whether the launcher may fetch somebody
/// else's emulator for them.
///
/// The offer is only ever an offer. Doing it by hand stays on the same screen,
/// as a button rather than as small print, and it is the choice the launcher
/// falls back to whenever anything at all is missing -- an incomplete source, a
/// release that cannot be read, a download that fails.
///
/// Everything that makes this consent rather than a click is on screen BEFORE
/// the button can be pressed: whose program it is, under what licence, the
/// licence's own address, the page a person would visit themselves, and the
/// exact file that would be fetched, by name and size, from that project's own
/// release. Pressing the button is then the same act as downloading it from
/// that page by hand -- which is precisely the claim being made, so the dialog
/// has to make it true.
public static class EmulatorInstallOffer
{
    private static readonly Brush Fg       = Frozen(0xCC, 0xD0, 0xE0);
    private static readonly Brush Muted    = Frozen(0x72, 0x7A, 0x99);
    private static readonly Brush PanelBg  = Frozen(0x10, 0x14, 0x22);
    private static readonly Brush WindowBg = Frozen(0x0A, 0x0D, 0x18);
    private static readonly Brush BorderBr = Frozen(0x2A, 0x30, 0x50);
    private static readonly Brush Accent   = Frozen(0xE0, 0xB0, 0x50);
    private static readonly Brush Error    = Frozen(0xE0, 0x60, 0x60);
    private static readonly Brush BtnBg    = Frozen(0x1A, 0x1E, 0x30);

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    private const int ReadDelaySeconds = 4;

    /// Ask, and -- if the player says yes -- install.
    ///
    /// Returns the path to the installed executable, or null when they chose to
    /// do it themselves, cancelled, or the install did not work out. A null is
    /// never an error the caller has to explain: the dialog has already said
    /// what happened, and the manual route is still open.
    public static async Task<string?> RunAsync(
        Window? owner, EmulatorRequirement req, string emulatorsRoot)
    {
        // No complete declaration, no offer. The player is pointed at the page
        // and installs it themselves, exactly as before this existed.
        if (!req.CanOfferInstall) return null;

        var dlg = new OfferWindow(req, req.Source!, emulatorsRoot) { Owner = owner };
        dlg.Show();
        await dlg.Ready.Task;
        return dlg.InstalledExe;
    }

    private sealed class OfferWindow : Window
    {
        public readonly TaskCompletionSource<bool> Ready = new();
        public string? InstalledExe;

        private readonly EmulatorRequirement _req;
        private readonly EmulatorSource _src;
        private readonly string _root;

        private readonly StackPanel _root2 = new() { Margin = new Thickness(22) };
        private readonly TextBlock  _asset;
        private readonly Button     _install;
        private readonly DispatcherTimer _timer;
        private int _remaining = ReadDelaySeconds;

        public OfferWindow(EmulatorRequirement req, EmulatorSource src, string root)
        {
            _req = req; _src = src; _root = root;

            Title = "Install " + req.DisplayName;
            Width = 600;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = WindowBg;

            _root2.Children.Add(T(req.DisplayName, 18, FontWeights.Bold, Fg));
            _root2.Children.Add(T("by " + src.Author, 12, FontWeights.Normal, Muted,
                                  new Thickness(0, 2, 0, 14)));

            // The heart of it. This program is not ours, and the sentence that
            // says so comes before the one offering to fetch it.
            _root2.Children.Add(T(
                $"{req.DisplayName} is not part of Multiworld Launcher. It is "
              + $"{src.Author}'s program, released by them under {src.Licence}. "
              + "The launcher only needs it to be on your computer; it does not "
              + "include it, and does not claim any part of it.",
                12, FontWeights.Normal, Fg, new Thickness(0, 0, 0, 14)));

            var box = new Border
            {
                Background = PanelBg, BorderBrush = Accent, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4), Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 0, 14),
            };
            var inner = new StackPanel();
            inner.Children.Add(T("What you would be downloading", 13, FontWeights.SemiBold, Accent,
                                 new Thickness(0, 0, 0, 8)));
            inner.Children.Add(Row("Made by", src.Author));
            inner.Children.Add(Row("Licence", src.Licence));
            inner.Children.Add(Link("Licence text", src.LicenceUrl));
            inner.Children.Add(Link("Download page", src.DownloadPage));

            // Filled in once the release has actually been read. Naming the file
            // only after asking the project itself means the dialog cannot
            // promise a download it has not found.
            _asset = T("File   reading " + src.Owner + "/" + src.Repo + "'s releases...",
                       12, FontWeights.Normal, Muted, new Thickness(0, 6, 0, 0));
            inner.Children.Add(_asset);

            inner.Children.Add(T(
                $"It would be unpacked into  Emulators\\{req.FolderName}\\  and nothing "
              + "else on your computer is touched. A SOURCE.txt is written beside "
              + "the files with the author, the licence and the address it came "
              + "from, so the folder can still answer that question later.",
                11, FontWeights.Normal, Muted, new Thickness(0, 10, 0, 0)));
            box.Child = inner;
            _root2.Children.Add(box);

            _root2.Children.Add(T(
                "Choosing to download is the same act as opening the download page "
              + "and installing it yourself -- the launcher fetches that one file "
              + "from that one address on your behalf. If you would rather do it "
              + "by hand, that works exactly as well and always will.",
                12, FontWeights.Normal, Fg, new Thickness(0, 0, 0, 4)));

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0),
            };

            var cancel = MakeButton("Not now", isDefault: true);
            cancel.Click += (_, _) => Close();

            var manual = MakeButton("I will install it myself");
            manual.Margin = new Thickness(10, 0, 0, 0);
            manual.Click += (_, _) => { Manual(); Close(); };

            _install = MakeButton($"I have read the above -- download and install  ({_remaining})");
            _install.Margin = new Thickness(10, 0, 0, 0);
            _install.IsEnabled = false;
            _install.Click += async (_, _) => await InstallAsync();

            row.Children.Add(cancel);
            row.Children.Add(manual);
            row.Children.Add(_install);
            _root2.Children.Add(row);

            Content = _root2;

            // The countdown exists so that reading is the default rather than a
            // thing you could have done. It only starts once the release has
            // been read -- there is nothing to consent to until the file the
            // launcher would fetch has a name.
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) =>
            {
                _remaining--;
                if (_remaining > 0)
                {
                    _install.Content =
                        $"I have read the above -- download and install  ({_remaining})";
                    return;
                }
                _timer.Stop();
                _install.Content = "I have read the above -- download and install";
                _install.IsEnabled = true;
            };

            Closed += (_, _) => { _timer.Stop(); Ready.TrySetResult(true); };
            Loaded += async (_, _) => await LookUpAsync();
        }

        private async Task LookUpAsync()
        {
            var found = await EmulatorInstaller.FindAssetAsync(_src);
            if (found is null)
            {
                // Nothing was found, so nothing is offered. Saying "install it
                // yourself" here is the honest answer, not a fallback.
                _asset.Text =
                    $"No file matching \"{_src.AssetPattern}\" is in "
                  + $"{_src.Owner}/{_src.Repo}'s releases right now, so the launcher "
                  + "will not guess. Use \"I will install it myself\".";
                _asset.Foreground = Error;
                return;
            }

            var (_, name, size, tag) = found.Value;
            string mb = size > 0 ? $"  ({size / 1024.0 / 1024.0:0.#} MB)" : "";
            _asset.Text = $"File   {name}{mb}   from release {tag}";
            _asset.Foreground = Fg;
            _timer.Start();
        }

        /// The route that was always there: their page, and their folder, open
        /// side by side so the download has somewhere obvious to go.
        private void Manual()
        {
            try
            {
                Process.Start(new ProcessStartInfo(_src.DownloadPage) { UseShellExecute = true });
                string dest = System.IO.Path.Combine(_root, _req.FolderName);
                System.IO.Directory.CreateDirectory(dest);
                Process.Start(new ProcessStartInfo(dest) { UseShellExecute = true });
            }
            catch { /* the player can still browse there themselves */ }
        }

        private async Task InstallAsync()
        {
            // Consent is given; from here the window is a progress report.
            _root2.Children.Clear();
            _root2.Children.Add(T(_req.DisplayName, 18, FontWeights.Bold, Fg));
            _root2.Children.Add(T($"by {_src.Author}, {_src.Licence}", 12,
                                  FontWeights.Normal, Muted, new Thickness(0, 2, 0, 16)));

            var status = T("Starting...", 12, FontWeights.Normal, Fg);
            var bar = new ProgressBar
            {
                Height = 6, Minimum = 0, Maximum = 100,
                Margin = new Thickness(0, 10, 0, 0),
                Foreground = Accent, Background = PanelBg, BorderBrush = BorderBr,
            };
            _root2.Children.Add(status);
            _root2.Children.Add(bar);

            var progress = new Progress<EmulatorInstaller.Progress>(p =>
            {
                status.Text = p.Stage;
                bar.Value = p.Percent;
            });

            var result = await EmulatorInstaller.InstallAsync(
                _req, _src, _root, progress);

            status.Text = result.Message;
            status.Foreground = result.Ok ? Fg : Error;
            bar.Value = result.Ok ? 100 : 0;

            if (result.Ok) InstalledExe = result.ExePath;

            var close = MakeButton(result.Ok ? "Done" : "Close", isDefault: true);
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.Margin = new Thickness(0, 20, 0, 0);
            close.Click += (_, _) => Close();

            // A failed download leaves the manual route one click away rather
            // than at the end of a sentence the player has to act on later.
            if (!result.Ok)
            {
                var again = MakeButton("Open the download page");
                again.HorizontalAlignment = HorizontalAlignment.Right;
                again.Margin = new Thickness(0, 12, 0, 0);
                again.Click += (_, _) => Manual();
                _root2.Children.Add(again);
            }

            _root2.Children.Add(close);
        }

        private static TextBlock T(string s, double size, FontWeight weight, Brush brush,
                                   Thickness? margin = null)
            => new()
            {
                Text = s, FontSize = size, FontWeight = weight, Foreground = brush,
                TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0),
            };

        private static TextBlock Row(string label, string value)
            => T($"{label}   {value}", 12, FontWeights.Normal, Fg, new Thickness(0, 3, 0, 0));

        /// A real link, because "read the licence" is not an instruction the
        /// player can follow if they have to retype the address.
        private static TextBlock Link(string label, string url)
        {
            var link = new Hyperlink(new Run(url)) { Foreground = Accent };
            link.RequestNavigate += (_, e) => e.Handled = true;
            link.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch { }
            };

            var tb = new TextBlock
            {
                FontSize = 12, Foreground = Fg, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            };
            tb.Inlines.Add(new Run(label + "   ") { Foreground = Fg });
            tb.Inlines.Add(link);
            return tb;
        }

        private static Button MakeButton(string content, bool isDefault = false) => new()
        {
            Content = content,
            Padding = new Thickness(16, 8, 16, 8),
            Background = BtnBg,
            Foreground = Fg,
            BorderBrush = BorderBr,
            BorderThickness = new Thickness(1),
            IsDefault = isDefault,
            IsCancel = isDefault,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
    }
}
