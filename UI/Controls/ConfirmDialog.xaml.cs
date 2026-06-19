using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

// (WPF/WinForms disambiguation handled in GlobalUsings.cs)

namespace LauncherV2.UI.Controls;

// ═══════════════════════════════════════════════════════════════════════════════
// ConfirmDialog — themed in-app replacement for MessageBox (P3-22).
//
// The launcher is a fully custom-chromed dark app; the stock gray Win32
// MessageBox broke the visual language on every confirmation. This window
// covers the three shapes the launcher needs:
//
//   Show(...)           → confirm/cancel    (returns bool)
//   ShowInfo(...)       → single OK         (returns nothing)
//   ShowThreeWay(...)   → Yes / No / Cancel (returns MessageBoxResult)
//
// `danger: true` paints the primary button red — used for irreversible
// actions (stop game, release items) so the destructive choice never looks
// like the safe gold default. Esc always cancels; Enter fires the primary.
// Must be called on the UI thread with a visible owner window.
// ═══════════════════════════════════════════════════════════════════════════════

public partial class ConfirmDialog : Window
{
    private MessageBoxResult _result = MessageBoxResult.Cancel;

    private ConfirmDialog(
        Window owner, string title, string message,
        string yesText, string? noText, string? cancelText, bool danger)
    {
        InitializeComponent();
        Owner = owner;

        TxtDlgTitle.Text = title;
        TxtDlgBody.Text  = message;
        BtnDlgYes.Content = yesText;

        // Icon: warning triangle for destructive actions, info circle otherwise
        TxtDlgIcon.Text       = danger ? "⚠" : "ⓘ";
        TxtDlgIcon.Foreground = danger
            ? (Brush)FindResource("BrushError")
            : (Brush)FindResource("BrushAccent");

        if (danger)
        {
            BtnDlgYes.Background = (Brush)FindResource("BrushError");
            BtnDlgYes.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xE8, 0xE8));
        }

        if (noText != null)
        {
            BtnDlgNo.Content    = noText;
            BtnDlgNo.Visibility = Visibility.Visible;
        }
        if (cancelText != null)
        {
            BtnDlgCancel.Content    = cancelText;
            BtnDlgCancel.Visibility = Visibility.Visible;
        }
    }

    // ── Static entry points ───────────────────────────────────────────────────

    /// Two-button confirm. Returns true when the user clicked the confirm
    /// button, false on Cancel / Esc / closing the dialog.
    public static bool Show(
        Window owner, string title, string message,
        string confirmText = "OK", string cancelText = "Cancel",
        bool danger = false)
    {
        var dlg = new ConfirmDialog(owner, title, message,
                                    confirmText, noText: null, cancelText, danger);
        dlg.ShowDialog();
        return dlg._result == MessageBoxResult.Yes;
    }

    /// Single-OK information box (themed MessageBox.Show replacement).
    public static void ShowInfo(
        Window owner, string title, string message, string okText = "OK")
    {
        var dlg = new ConfirmDialog(owner, title, message,
                                    okText, noText: null, cancelText: null,
                                    danger: false);
        dlg.ShowDialog();
    }

    /// Three-way choice. Returns Yes / No / Cancel; Esc and closing → Cancel.
    public static MessageBoxResult ShowThreeWay(
        Window owner, string title, string message,
        string yesText, string noText, string cancelText = "Cancel",
        bool danger = false)
    {
        var dlg = new ConfirmDialog(owner, title, message,
                                    yesText, noText, cancelText, danger);
        dlg.ShowDialog();
        return dlg._result;
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────

    private void BtnYes_Click(object sender, RoutedEventArgs e)
    {
        _result = MessageBoxResult.Yes;
        Close();
    }

    private void BtnNo_Click(object sender, RoutedEventArgs e)
    {
        _result = MessageBoxResult.No;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _result = MessageBoxResult.Cancel;
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // IsCancel only works while the Cancel button is visible — info boxes
        // and plain confirms still need Esc to dismiss.
        if (e.Key == Key.Escape)
        {
            _result = MessageBoxResult.Cancel;
            Close();
        }
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // The dialog has no title bar — let the user drag it by the card.
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
