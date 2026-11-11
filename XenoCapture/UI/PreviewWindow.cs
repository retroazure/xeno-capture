using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace XenoCapture.UI;

// Keep the message pump alive while DXGI stops.
internal sealed class PreviewWindow : Window
{
    readonly Direct3DHost host;
    readonly bool animateEntry;
    readonly Func<Rectangle>? displayBounds;
    readonly Func<bool>? motionEnabled;
    readonly Func<Rectangle?>? dockTarget;
    readonly Func<Task, Task>? resumeAtLanding;
    readonly TaskCompletionSource skipLandingWait = new(TaskCreationOptions.RunContinuationsAsynchronously);
    FullscreenWindowPop? windowPop;
    HwndSource? source;
    internal bool EntryRegionActive => windowPop?.RegionActive == true;
    internal int EntryRegionUpdates => windowPop?.RegionUpdates ?? 0;
    internal Rectangle? LandingBounds => windowPop?.LandingBounds;
    internal Rectangle? FirstDockBounds => windowPop?.FirstDockBounds;
    internal bool HoldsLandingSurface => windowPop?.HoldsLandingSurface == true;
    readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    bool stopping, mayClose, skipMotion;
    Exception? closeError;

    public PreviewWindow(Direct3DHost host, bool animateEntry = false, Func<Rectangle>? displayBounds = null,
        Func<bool>? motionEnabled = null, Func<Rectangle?>? dockTarget = null, Func<Task, Task>? resumeAtLanding = null)
    {
        this.host = host;
        this.animateEntry = animateEntry;
        this.displayBounds = displayBounds;
        this.motionEnabled = motionEnabled;
        this.dockTarget = dockTarget;
        this.resumeAtLanding = resumeAtLanding;
        Content = host;
        if (displayBounds != null) Loaded += (_, _) => FitDisplay();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        nint handle = new WindowInteropHelper(this).Handle;
        if (displayBounds != null)
        {
            source = HwndSource.FromHwnd(handle);
            source?.AddHook(DisplayMessage);
            FitDisplay();
        }
        if (animateEntry || motionEnabled != null) windowPop = new FullscreenWindowPop(handle, host, animateEntry);
    }

    void FitDisplay()
    {
        if (stopping || displayBounds == null || source == null || source.IsDisposed) return;
        var bounds = displayBounds();
        if (!PositionWindow(source.Handle, bounds)) throw new Win32Exception(Marshal.GetLastWin32Error());
        host.SetFullscreenViewportSize(bounds.Width, bounds.Height);
    }

    internal static bool PositionWindow(nint hwnd, Rectangle bounds)
    {
        const uint keepOrderAndFocus = 0x0214; // NOOWNERZORDER | NOACTIVATE | NOZORDER
        return SetWindowPos(hwnd, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, keepOrderAndFocus);
    }

    nint DisplayMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message is 0x007E or 0x02E0) // WM_DISPLAYCHANGE / WM_DPICHANGED
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FitDisplay));
        return 0;
    }

    public Task CloseAsync(bool animate = true)
    {
        if (!animate)
        {
            skipMotion = true;
            skipLandingWait.TrySetResult();
            windowPop?.Stop(clear: false);
        }
        if (!stopping && !closed.Task.IsCompleted) Close();
        return closed.Task;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (mayClose) { base.OnClosing(e); return; }
        base.OnClosing(e);
        e.Cancel = true;
        if (stopping) return;
        stopping = true;
        await Dispatcher.Yield(DispatcherPriority.Background);
        bool landed = false;
        try
        {
            if (!skipMotion && (motionEnabled?.Invoke() ?? animateEntry) && windowPop != null && dockTarget?.Invoke() is { } target)
                landed = await windowPop.PlayExitAsync(target);
        }
        catch (Exception ex) { closeError = ex; }
        finally { windowPop?.Stop(clear: false); }
        try { await host.StopAsync(); } catch (Exception ex) { closeError ??= ex; }
        if (landed && !skipMotion && resumeAtLanding != null)
        {
            try { await resumeAtLanding(skipLandingWait.Task); } catch (Exception ex) { closeError ??= ex; }
        }
        mayClose = true;
        try { Close(); } catch (Exception ex) { closed.TrySetException(closeError ?? ex); }
    }

    protected override void OnClosed(EventArgs e)
    {
        windowPop?.Stop(clear: false);
        try { host.Dispose(); } catch (Exception ex) { closeError ??= ex; }
        finally { windowPop?.ReleaseLandingSurface(); }
        try { base.OnClosed(e); } catch (Exception ex) { closeError ??= ex; }
        if (closeError == null) closed.TrySetResult(); else closed.TrySetException(closeError);
    }

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
