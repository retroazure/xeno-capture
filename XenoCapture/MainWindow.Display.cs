using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Screen = System.Windows.Forms.Screen;

namespace XenoCapture;

public partial class MainWindow
{
    sealed record DisplayChoice(string? Device, string Label);
    string? fullscreenDisplayDevice;
    bool refreshingFullscreenDisplays;

    void FullscreenDisplayOpened(object sender, EventArgs e) => RefreshFullscreenDisplays();

    void RefreshFullscreenDisplays()
    {
        var choices = Screen.AllScreens.OrderBy(screen => screen.DeviceName, StringComparer.OrdinalIgnoreCase)
            .Select(screen => new DisplayChoice(screen.DeviceName,
                $"{screen.DeviceName[(screen.DeviceName.LastIndexOf('\\') + 1)..]} · {screen.Bounds.Width} × {screen.Bounds.Height}"
                + (screen.Primary ? " · Primary" : "")))
            .Prepend(new DisplayChoice(null, "Same display as the app")).ToList();
        if (fullscreenDisplayDevice != null && choices.All(choice => choice.Device != fullscreenDisplayDevice))
            choices.Add(new DisplayChoice(fullscreenDisplayDevice, "Saved display unavailable · Using app display"));

        refreshingFullscreenDisplays = true;
        try
        {
            FullscreenDisplay.DisplayMemberPath = nameof(DisplayChoice.Label);
            FullscreenDisplay.ItemsSource = choices;
            FullscreenDisplay.SelectedItem = choices.Find(choice => choice.Device == fullscreenDisplayDevice);
        }
        finally { refreshingFullscreenDisplays = false; }
    }

    void FullscreenDisplayChanged(object sender, SelectionChangedEventArgs e)
    {
        if (refreshingFullscreenDisplays || FullscreenDisplay.SelectedItem is not DisplayChoice choice) return;
        fullscreenDisplayDevice = choice.Device;
        if (IsLoaded) SaveSettings();
    }

    System.Drawing.Rectangle ResolveFullscreenBounds()
    {
        if (fullscreenDisplayDevice != null)
        {
            var selected = Screen.AllScreens.FirstOrDefault(screen => screen.DeviceName == fullscreenDisplayDevice);
            if (selected != null) return selected.Bounds;
        }
        return Screen.FromHandle(new WindowInteropHelper(this).Handle).Bounds;
    }

    System.Drawing.Rectangle? EmbeddedPreviewBounds()
    {
        if (!IsVisible || WindowState == WindowState.Minimized || !PreviewSurface.IsVisible || PresentationSource.FromVisual(PreviewSurface) == null)
            return null;
        var topLeft = PreviewSurface.PointToScreen(new Point());
        var bottomRight = PreviewSurface.PointToScreen(new Point(PreviewSurface.ActualWidth, PreviewSurface.ActualHeight));
        var bounds = System.Drawing.Rectangle.FromLTRB((int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y),
            (int)Math.Round(bottomRight.X), (int)Math.Round(bottomRight.Y));
        return bounds.Width > 1 && bounds.Height > 1 ? bounds : null;
    }

    void FullscreenEdgeSmoothingChanged(object sender, RoutedEventArgs e)
    {
        if (fullscreenPreview != null) fullscreenPreview.EdgeSmoothing = FullscreenEdgeSmoothing.IsChecked == true;
        if (IsLoaded) SaveSettings();
    }
}
