// ── Disambiguate WPF vs WinForms types ────────────────────────────────────────
// Adding UseWindowsForms=true to enable NotifyIcon brings in System.Windows.Forms
// and System.Drawing which clash with WPF types.  Pin the WPF types globally so
// every .cs file in the project sees the correct defaults without change.
// SystemTrayManager.cs must use fully-qualified System.Drawing.*/System.Windows.Forms.* names.

global using Application        = System.Windows.Application;
global using Brush              = System.Windows.Media.Brush;
global using Brushes            = System.Windows.Media.Brushes;
global using Color              = System.Windows.Media.Color;
global using Cursors            = System.Windows.Input.Cursors;
global using HorizontalAlignment = System.Windows.HorizontalAlignment;
global using Image              = System.Windows.Controls.Image;
global using KeyEventArgs       = System.Windows.Input.KeyEventArgs;
global using MessageBox         = System.Windows.MessageBox;
global using MessageBoxButton   = System.Windows.MessageBoxButton;
global using MessageBoxImage    = System.Windows.MessageBoxImage;
global using MessageBoxResult   = System.Windows.MessageBoxResult;
global using Button             = System.Windows.Controls.Button;
global using CheckBox           = System.Windows.Controls.CheckBox;
global using Panel              = System.Windows.Controls.Panel;
global using ComboBox           = System.Windows.Controls.ComboBox;
global using Orientation        = System.Windows.Controls.Orientation;
global using ProgressBar        = System.Windows.Controls.ProgressBar;
global using TextBox            = System.Windows.Controls.TextBox;
global using DragDropEffects    = System.Windows.DragDropEffects;
