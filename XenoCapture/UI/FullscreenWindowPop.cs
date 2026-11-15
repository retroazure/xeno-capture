using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using XenoCapture.Engine;

namespace XenoCapture.UI;

internal static class FullscreenMotion
{
    internal const long Duration = 3_400_000; // 340 ms in capture-clock ticks.


    internal static float Scale(long elapsed)
    {
        double progress = Math.Clamp(elapsed / (double)Duration, 0, 1);
        if (progress < .68)
        {
            double remaining = 1 - progress / .68;
            return (float)(.994 - .414 * remaining * remaining * remaining);
        }
        if (progress < .84)
            return (float)(.994 - .019 * Smooth((progress - .68) / .16));
        return (float)(.975 + .025 * Smooth((progress - .84) / .16));
    }

    internal static double Smooth(double progress) => progress * progress * (3 - 2 * progress);
}

// The UI window and the video draw use the same screen-space animation sample.
internal sealed class FullscreenDockMotion(Rectangle window, Rectangle start, Rectangle target, int startDiameter,
    Action? notifyFrame = null)
{
    readonly bool fixedWindow = window.Contains(target);
    internal Size CanvasSize { get; } = new(Math.Max(window.Width, target.Width), Math.Max(window.Height, target.Height));
    long startedAt, presentedAt;
    internal long PresentedAt => Volatile.Read(ref presentedAt);

    internal void Presented(long sampleTime)
    {
        if (startedAt == 0) Volatile.Write(ref startedAt, sampleTime);
        Volatile.Write(ref presentedAt, sampleTime);
        notifyFrame?.Invoke();
    }
    internal bool Landed { get; set; }
    internal XenoCapture.Engine.Com? Surface { get; set; }

    internal (Rectangle Window, Rectangle Clip, int Diameter, bool Complete) Sample(long now)
    {
        long started = Volatile.Read(ref startedAt);
        double progress = started == 0 ? 0 : Math.Clamp((now - started) / (double)FullscreenMotion.Duration, 0, 1);
        double eased = FullscreenMotion.Smooth(progress);
        int Blend(int a, int b) => (int)Math.Round(a + (b - a) * eased);
        var visible = new Rectangle(Blend(start.X, target.X), Blend(start.Y, target.Y),
            Blend(start.Width, target.Width), Blend(start.Height, target.Height));
        // On the same display we can dock by clipping a stationary swap chain.
        // A destination outside it needs the fixed-size canvas to travel.
        var outer = fixedWindow ? window : new Rectangle(
            visible.X - (CanvasSize.Width - visible.Width) / 2,
            visible.Y - (CanvasSize.Height - visible.Height) / 2, CanvasSize.Width, CanvasSize.Height);
        var clip = new Rectangle(visible.X - outer.X, visible.Y - outer.Y, visible.Width, visible.Height);
        int diameter = (int)Math.Round(startDiameter * (1 - eased) + 64 * Math.Sin(Math.PI * progress));
        return (outer, clip, diameter, progress >= 1);
    }
}

// Change the visible region of the real window while keeping its swap chain at a stable size.
internal sealed class FullscreenWindowPop
{
    readonly nint hwnd;
    readonly Direct3DHost host;
    readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    readonly Action applyQueuedDockFrame;
    int dockFrameQueued;
    readonly long openedAt = Clock.Now;
    (int Left, int Top, int Right, int Bottom, int Diameter) previous;
    bool rendering;
    TaskCompletionSource? exit;
    FullscreenDockMotion? docking;
    long appliedSample;
    Rectangle dockWindow;
    internal int DockWindowMoves { get; private set; }
    internal Rectangle? LandingBounds { get; private set; }
    internal Rectangle? FirstDockBounds { get; private set; }
    internal bool HoldsLandingSurface => docking?.Surface != null;
    internal bool RegionActive { get; private set; }
    internal int RegionUpdates { get; private set; }

    internal FullscreenWindowPop(nint hwnd, Direct3DHost host, bool animateEntry)
    {
        this.hwnd = hwnd;
        this.host = host;
        applyQueuedDockFrame = ApplyQueuedDockFrame;
        if (animateEntry)
        {
            Apply(0);
            StartRendering();
        }
    }

    void StartRendering()
    {
        if (rendering) return;
        CompositionTarget.Rendering += Render;
        rendering = true;
    }

    internal async Task<bool> PlayExitAsync(Rectangle target)
    {
        if (!GetWindowRect(hwnd, out var bounds)) return false;
        var window = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        var start = RegionActive
            ? Rectangle.FromLTRB(window.X + previous.Left, window.Y + previous.Top, window.X + previous.Right, window.Y + previous.Bottom)
            : window;
        if (start.Width < 2 || start.Height < 2) return false;

        var motion = new FullscreenDockMotion(window, start, target, RegionActive ? previous.Diameter : 0, QueueDockFrame);
        dockWindow = window;
        var first = motion.Sample(0);
        if (first.Window != window && !ApplyDockSample(first.Window, first.Clip, first.Diameter)) return false;

        exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        docking = motion;
        if (!host.StartFullscreenExit(motion)) return false;
        StartRendering();
        // Bound teardown if the desktop or renderer stops delivering frames.
        await Task.WhenAny(exit.Task, Task.Delay(TimeSpan.FromMilliseconds(FullscreenMotion.Duration / 10_000.0 + 180)));
        return motion.Landed;
    }

    internal void ReleaseLandingSurface()
    {
        docking?.Surface?.Dispose();
        if (docking != null) docking.Surface = null;
    }

    void QueueDockFrame()
    {
        // Apply the latest accepted video pose without waiting for another WPF
        // composition tick. A busy dispatcher only needs the newest pose.
        if (Interlocked.Exchange(ref dockFrameQueued, 1) == 0)
            dispatcher.BeginInvoke(DispatcherPriority.Render, applyQueuedDockFrame);
    }

    void ApplyQueuedDockFrame()
    {
        Volatile.Write(ref dockFrameQueued, 0);
        if (rendering && docking is { } motion) ApplyLatestDockFrame(motion);
    }

    void ApplyLatestDockFrame(FullscreenDockMotion motion)
    {
        long sampleTime = motion.PresentedAt;
        if (sampleTime == 0 || sampleTime == appliedSample) return;
        var sample = motion.Sample(sampleTime);
        if (!ApplyDockSample(sample.Window, sample.Clip, sample.Diameter)) return;
        appliedSample = sampleTime;
        var visible = sample.Clip;
        visible.Offset(sample.Window.Location);
        FirstDockBounds ??= visible;
        if (sample.Complete)
        {
            LandingBounds = visible;
            motion.Landed = true;
            Stop(clear: false);
        }
    }

    void Render(object? sender, EventArgs e)
    {
        if (docking is { } motion)
        {
            // Retry a move if a DPI change interrupted it.
            ApplyLatestDockFrame(motion);
            return;
        }
        long now = Clock.Now;
        long start = host.FullscreenEntryStarted;
        // Give a cold renderer time to show its first image, then run the
        // window motion even if the input or AI startup is still waiting.
        if (start == 0 && now - openedAt >= 3_000_000)
            start = host.StartFullscreenEntry(now);
        long elapsed = start == 0 ? 0 : now - start;
        if (elapsed >= FullscreenMotion.Duration) Stop(clear: true);
        else Apply(elapsed);
    }

    bool ApplyDockSample(Rectangle window, Rectangle clip, int diameter)
    {
        if (dockWindow != window)
        {
            if (!PreviewWindow.PositionWindow(hwnd, window) || !GetWindowRect(hwnd, out var actual)) return false;
            // A DPI change can reposition a moving window. Retry on the next composition tick.
            if (Rectangle.FromLTRB(actual.Left, actual.Top, actual.Right, actual.Bottom) != window) return false;
            dockWindow = window;
            DockWindowMoves++;
        }
        return ApplyRegion(clip, diameter);
    }

    void Apply(long elapsed)
    {
        if (!GetWindowRect(hwnd, out var bounds)) return;
        int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
        if (width < 2 || height < 2) return;
        float scale = FullscreenMotion.Scale(elapsed);
        int x = (int)Math.Round(width * (1 - scale) / 2);
        int y = (int)Math.Round(height * (1 - scale) / 2);
        double remaining = 1 - Math.Clamp(elapsed / (double)FullscreenMotion.Duration, 0, 1);
        int diameter = Math.Max(2, (int)Math.Round(Math.Clamp(height * .085, 26, 96) * remaining));
        ApplyRegion(Rectangle.FromLTRB(x, y, width - x, height - y), diameter);
    }

    bool ApplyRegion(Rectangle clip, int diameter)
    {
        var shape = (clip.Left, clip.Top, clip.Right, clip.Bottom, diameter);
        if (shape == previous) return true;
        nint region = diameter > 1
            ? CreateRoundRectRgn(clip.Left, clip.Top, clip.Right, clip.Bottom, diameter, diameter)
            : CreateRectRgn(clip.Left, clip.Top, clip.Right, clip.Bottom);
        if (region == 0) return false;
        if (SetWindowRgn(hwnd, region, true) == 0)
        {
            DeleteObject(region);
            return false;
        }
        // Windows takes ownership after a successful SetWindowRgn call.
        previous = shape;
        RegionActive = true;
        RegionUpdates++;
        return true;
    }

    internal void Stop(bool clear)
    {
        if (rendering) { CompositionTarget.Rendering -= Render; rendering = false; }
        if (clear && RegionActive && SetWindowRgn(hwnd, 0, true) != 0)
        {
            RegionActive = false;
            previous = default;
        }
        exit?.TrySetResult();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WindowRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetWindowRect(nint window, out WindowRect rect);

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    static extern nint CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DeleteObject(nint handle);
}
