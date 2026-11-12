using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Cursor = System.Windows.Forms.Cursor;

namespace XenoCapture.UI;

// The native video HWND sits above WPF content. Open the overlay only while it is needed.
internal sealed class FullscreenExitControl : IDisposable
{
    const string Label = "Exit fullscreen";
    const double ButtonWidth = 148, ButtonHeight = 42, Inset = 14;

    readonly PreviewWindow window;
    readonly Direct3DHost host;
    readonly Popup popup;
    readonly Button button;
    readonly DispatcherTimer pointerCheck;

    internal FullscreenExitControl(PreviewWindow window, Direct3DHost host, Action requestExit)
    {
        this.window = window;
        this.host = host;

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Text = "\uE73F", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0)
        });
        content.Children.Add(new TextBlock { Text = Label, VerticalAlignment = VerticalAlignment.Center });
        button = new Button
        {
            Content = content, Width = ButtonWidth, Height = ButtonHeight,
            Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Brushes.White,
            Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : new SolidColorBrush(Color.FromRgb(30, 31, 37)),
            BorderBrush = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : new SolidColorBrush(Color.FromRgb(104, 109, 124))
        };
        AutomationProperties.SetName(button, Label);
        button.Click += (_, _) => { Hide(); requestExit(); };

        popup = new Popup
        {
            Child = button, PlacementTarget = host, Placement = PlacementMode.Relative,
            VerticalOffset = Inset, AllowsTransparency = true
        };
        pointerCheck = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        pointerCheck.Tick += CheckPointer;
        host.MessageHook += OnVideoMessage;
        window.Deactivated += OnDeactivated;
    }

    internal Button? VisibleButton => popup.IsOpen ? button : null;

    nint OnVideoMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int mouseMove = 0x0200;
        if (message != mouseMove || !window.IsVisible || window.EntryRegionActive) return 0;

        var dpi = VisualTreeHelper.GetDpi(host);
        int position = unchecked((int)lParam);
        var point = new Point(unchecked((short)position) / dpi.DpiScaleX,
            unchecked((short)(position >> 16)) / dpi.DpiScaleY);
        if (!InRevealCorner(point)) Hide();
        else if (!popup.IsOpen)
        {
            popup.HorizontalOffset = host.ActualWidth - ButtonWidth - Inset;
            popup.IsOpen = true;
            pointerCheck.Start();
        }
        return 0;
    }

    bool InRevealCorner(Point point) =>
        host.ActualWidth >= ButtonWidth + 2 * Inset && host.ActualHeight >= ButtonHeight + 2 * Inset &&
        point.X >= host.ActualWidth - ButtonWidth - 2 * Inset && point.X < host.ActualWidth &&
        point.Y >= 0 && point.Y < ButtonHeight + 2 * Inset;

    void CheckPointer(object? sender, EventArgs e)
    {
        // Crossing onto another monitor can bypass the hosted window's mouse messages.
        var cursor = Cursor.Position;
        // WindowFromPoint can return the parent of a native static control.
        nint hovered = WindowFromPoint(cursor);
        if (hovered != 0 && PresentationSource.FromVisual(button) is HwndSource overlay && hovered == overlay.Handle) return;
        if (hovered != 0 && PresentationSource.FromVisual(host) is HwndSource source
            && (hovered == host.Handle || hovered == source.Handle)
            && InRevealCorner(host.PointFromScreen(new Point(cursor.X, cursor.Y)))) return;
        Hide();
    }

    void OnDeactivated(object? sender, EventArgs e) => Hide();

    internal void Hide()
    {
        pointerCheck.Stop();
        popup.IsOpen = false;
    }

    public void Dispose()
    {
        host.MessageHook -= OnVideoMessage;
        window.Deactivated -= OnDeactivated;
        Hide();
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", ExactSpelling = true)]
    static extern nint WindowFromPoint(System.Drawing.Point point);
}
