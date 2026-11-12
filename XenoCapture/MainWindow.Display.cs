using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using XenoCapture.Engine;
using XenoCapture.UI;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Screen = System.Windows.Forms.Screen;

namespace XenoCapture;

public partial class MainWindow
{
    sealed record DisplayChoice(string? Device, string Label)
    {
        public override string ToString() => Label;
    }
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

    async void Fullscreen(object sender, RoutedEventArgs e) => await RunUi(ToggleFullscreen);
    Task ToggleFullscreen() => ToggleFullscreenOnDisplay(discoverHardware ? ResolveFullscreenBounds : null);

    void SetFullscreenWindow(PreviewWindow? window)
    {
        fullscreen = window;
        FullscreenButton.Content = window == null ? "Fullscreen ⛶" : "Exit fullscreen";
        FullscreenButton.ToolTip = window == null
            ? "Fullscreen preview (F11). Hover the top-right corner to exit."
            : "Return the preview to this window";
    }

    async Task ToggleFullscreenOnDisplay(Func<System.Drawing.Rectangle>? displayBounds)
    {
        if (fullscreenTransition) return;
        fullscreenTransition = true;
        try
        {
            if (fullscreen != null)
            {
                PlayCue(InterfaceCue.FullscreenExit);
                await fullscreen.CloseAsync(animate: MotionEnabled);
                return;
            }
            if (!Connected || !sessions.Active || closing || Interlocked.Read(ref lastFrameAt) == 0) return;
            PlayCue(InterfaceCue.Tap);
            int session = sessions.Generation;
            await embeddedPreview.SuspendAsync();
            if (!sessions.IsCurrent(session) || closing || fullscreen != null) return;
            VideoHost.Visibility = Visibility.Collapsed;
            bool animateEntry = MotionEnabled;
            var host = NewPreview(isFullscreen: true, animateFullscreenEntry: animateEntry);
            host.Active = true;
            fullscreenPreview = host;
            activePreview = host;
            bool resumed = false;
            void ResumeEmbedded()
            {
                if (resumed || !sessions.IsCurrent(session) || closing) return;
                resumed = true;
                VideoHost.Visibility = Visibility.Visible;
                embeddedPreview.Active = true;
                activePreview = embeddedPreview;
                lock (frameGate) { if (latest != null) embeddedPreview.Submit(latest); }
            }

            async Task ResumeAtLanding(Task skipWait)
            {
                if (!sessions.IsCurrent(session) || closing) return;
                var embedded = embeddedPreview;
                var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Presented(PresentedFrame _) => firstFrame.TrySetResult();
                embedded.FramePresented += Presented;
                try
                {
                    ResumeEmbedded();
                    // The landing window covers the destination until the new swap chain draws.
                    await Task.WhenAny(firstFrame.Task, skipWait, Task.Delay(250));
                }
                finally { embedded.FramePresented -= Presented; }
            }

            var playWindow = new PreviewWindow(host, animateEntry, displayBounds, () => MotionEnabled, EmbeddedPreviewBounds, ResumeAtLanding,
                preview => PostUi(() => ReferenceEquals(fullscreen, preview) ? ToggleFullscreen() : Task.CompletedTask))
            {
                Title = "Xeno Capture — Fullscreen", Background = Brushes.Black, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, Width = 640, Height = 360, ShowActivated = discoverHardware,
                ShowInTaskbar = false, Owner = this
            };
            SetFullscreenWindow(playWindow);
            playWindow.PreviewKeyDown += async (_, e) =>
            {
                if (e.Key is Key.Escape or Key.F11) { e.Handled = true; await RunUi(ToggleFullscreen); }
            };
            playWindow.Closed += (_, _) =>
            {
                if (!ReferenceEquals(fullscreen, playWindow)) return;
                fullscreenPreview = null;
                SetFullscreenWindow(null);
                ResumeEmbedded();
            };
            try { playWindow.Show(); lock (frameGate) { if (latest != null) host.Submit(latest); } }
            catch
            {
                try { await playWindow.CloseAsync(animate: false); }
                finally
                {
                    if (ReferenceEquals(fullscreen, playWindow))
                    {
                        SetFullscreenWindow(null);
                        fullscreenPreview = null;
                        await host.StopAsync();
                        host.Dispose();
                        ResumeEmbedded();
                    }
                }
                throw;
            }
        }
        finally { fullscreenTransition = false; }
    }
    async void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.F11) { e.Handled = true; await RunUi(ToggleFullscreen); } }
}
