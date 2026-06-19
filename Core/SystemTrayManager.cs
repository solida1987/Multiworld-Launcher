using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
// Color is globally aliased to System.Windows.Media.Color (GlobalUsings.cs).
// This file needs System.Drawing.Color, so use a local alias.
using DrawingColor = System.Drawing.Color;

namespace LauncherV2.Core;

/// <summary>
/// Manages a Windows system tray icon.  Shown while a game is running so the user
/// can restore or stop the launcher without hunting for it on the taskbar.
/// </summary>
public sealed class SystemTrayManager : IDisposable
{
    // ── State ────────────────────────────────────────────────────────────────

    private NotifyIcon?  _notify;
    private bool         _disposed;

    // ── Events ───────────────────────────────────────────────────────────────

    /// <summary>User clicked "Open Launcher" from the tray menu.</summary>
    public event Action? OpenRequested;

    /// <summary>User clicked "Stop Game" from the tray menu.</summary>
    public event Action? StopGameRequested;

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Show the tray icon with the given tooltip.
    /// Call when the game process starts.
    /// </summary>
    public void Show(string tooltip = "Multiworld Launcher")
    {
        if (_disposed) return;

        EnsureCreated();
        _notify!.Text    = tooltip.Length > 63 ? tooltip[..63] : tooltip;  // WinForms limit
        _notify!.Visible = true;
    }

    /// <summary>
    /// Hide (but do not destroy) the tray icon.
    /// Call when the game process exits.
    /// </summary>
    public void Hide()
    {
        if (_notify != null)
            _notify.Visible = false;
    }

    /// <summary>Show a balloon tip notification from the tray icon.</summary>
    public void ShowBalloon(string title, string text, int timeoutMs = 3000)
    {
        if (_notify is { Visible: true })
            _notify.ShowBalloonTip(timeoutMs, title, text, ToolTipIcon.None);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_notify != null)
        {
            _notify.Visible = false;
            _notify.Icon?.Dispose();
            _notify.Dispose();
            _notify = null;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void EnsureCreated()
    {
        if (_notify != null) return;

        _notify = new NotifyIcon
        {
            Icon            = BuildProgrammaticIcon(),
            Visible         = false,
            ContextMenuStrip = BuildContextMenu(),
        };

        // Left-click / double-click → restore window
        _notify.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                OpenRequested?.Invoke();
        };
        _notify.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();

        var open = new ToolStripMenuItem("Open Launcher");
        open.Font  = new Font(open.Font, open.Font.Style | System.Drawing.FontStyle.Bold);
        open.Click += (_, _) => OpenRequested?.Invoke();

        var stop = new ToolStripMenuItem("Stop Game");
        stop.Click += (_, _) => StopGameRequested?.Invoke();

        var sep = new ToolStripSeparator();

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) =>
        {
            Hide();
            System.Windows.Application.Current?.Dispatcher.Invoke(
                () => System.Windows.Application.Current.Shutdown());
        };

        menu.Items.Add(open);
        menu.Items.Add(stop);
        menu.Items.Add(sep);
        menu.Items.Add(exit);

        return menu;
    }

    /// <summary>
    /// Build the tray icon programmatically — gold "AP" text on a dark
    /// background, no .ico file required. Drawn at the system's REAL small
    /// icon size (16 px at 100% DPI, larger when scaled): rendering 12 pt
    /// text into a 32×32 bitmap and letting Windows shrink it made the tray
    /// icon blurry at standard DPI (P3-12).
    /// </summary>
    private static Icon BuildProgrammaticIcon()
    {
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);

        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode   = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            // Background: dark rect
            using var bgBrush = new SolidBrush(DrawingColor.FromArgb(255, 13, 16, 24));   // #0D1018
            g.FillRectangle(bgBrush, 0, 0, size, size);

            // Border: gold, 1 px at native size
            using var pen = new Pen(DrawingColor.FromArgb(255, 204, 168, 0), 1f);         // #CCA800
            g.DrawRectangle(pen, 0, 0, size - 1, size - 1);

            // "AP" text in gold, scaled to the icon (8 px em at a 16 px icon —
            // the same half-height proportion the 32 px version used)
            using var textBrush = new SolidBrush(DrawingColor.FromArgb(255, 204, 168, 0));
            using var font      = new Font("Segoe UI", size * 0.5f,
                                           System.Drawing.FontStyle.Bold,
                                           GraphicsUnit.Pixel);
            var sf = new StringFormat
            {
                Alignment     = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString("AP", font, textBrush,
                         new RectangleF(0, 0.5f, size, size), sf);
        }

        // Convert Bitmap → Icon
        IntPtr hIcon = bmp.GetHicon();
        Icon   icon  = Icon.FromHandle(hIcon);

        // Clone so we can safely destroy the handle
        Icon safe = (Icon)icon.Clone();
        NativeMethods.DestroyIcon(hIcon);
        return safe;
    }

    // ── Minimal P/Invoke ──────────────────────────────────────────────────────

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(
            System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(IntPtr hIcon);
    }
}
