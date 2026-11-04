using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
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

    static double Smooth(double progress) => progress * progress * (3 - 2 * progress);
}

// The UI window and the video draw use the same screen-space animation sample.
internal sealed class FullscreenDockMotion(Rectangle window, Rectangle start, Rectangle target, long started)
{
    const long Duration = 2_800_000;
    readonly Size canvas = new(Math.Max(window.Width, target.Width), Math.Max(window.Height, target.Height));
    long presentedAt;
    internal long PresentedAt => Volatile.Read(ref presentedAt);
    internal void Presented(long sampleTime) => Volatile.Write(ref presentedAt, sampleTime);
    internal bool Landed { get; set; }
    internal Com? Surface { get; set; }

    internal (Rectangle Window, Rectangle Clip, int Diameter, bool Complete) Sample(long now)
    {
        double progress = Math.Clamp((now - started) / (double)Duration, 0, 1);
        double eased = progress * progress * (3 - 2 * progress);
        int Blend(int a, int b) => (int)Math.Round(a + (b - a) * eased);
        int width = Blend(start.Width, target.Width), height = Blend(start.Height, target.Height);
        var clip = new Rectangle((canvas.Width - width) / 2, (canvas.Height - height) / 2, width, height);
        var outer = new Rectangle(Blend(start.X, target.X) - clip.X, Blend(start.Y, target.Y) - clip.Y, canvas.Width, canvas.Height);
        int diameter = (int)Math.Round(64 * Math.Sin(Math.PI * progress));
        return (outer, clip, diameter, progress >= 1);
    }
}

// Change the visible region of the real window while keeping its swap chain at a stable size.
internal sealed class FullscreenWindowPop
{
    readonly nint hwnd;
    readonly Direct3DHost host;
    readonly long openedAt = Clock.Now;
    (int Left, int Top, int Right, int Bottom, int Diameter) previous;
    bool rendering;
    TaskCompletionSource? exit;
    FullscreenDockMotion? docking;
    long appliedSample;
    internal Rectangle? LandingBounds { get; private set; }
    internal Rectangle? FirstDockBounds { get; private set; }
    internal bool HoldsLandingSurface => docking?.Surface != null;
    internal bool RegionActive { get; private set; }
    internal int RegionUpdates { get; private set; }

    internal FullscreenWindowPop(nint hwnd, Direct3DHost host, bool animateEntry)
    {
        this.hwnd = hwnd;
        this.host = host;
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

        long now = Clock.Now;
        var motion = new FullscreenDockMotion(window, start, target, now);
        var first = motion.Sample(now);
        if (first.Window != window && (!PreviewWindow.PositionWindow(hwnd, first.Window) || !ApplyRegion(first.Clip, 0)))
            return false;

        exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        docking = motion;
        if (!host.StartFullscreenExit(motion)) return false;
        StartRendering();
        // Bound teardown if the desktop or renderer stops delivering frames.
        await Task.WhenAny(exit.Task, Task.Delay(TimeSpan.FromMilliseconds(460)));
        return motion.Landed;
    }

    internal void ReleaseLandingSurface()
    {
        docking?.Surface?.Dispose();
        if (docking != null) docking.Surface = null;
    }

    void Render(object? sender, EventArgs e)
    {
        long now = Clock.Now;
        if (docking is { } motion)
        {
            // Move the visible region only after its matching draw was queued.
            long sampleTime = motion.PresentedAt;
            if (sampleTime == 0 || sampleTime == appliedSample) return;
            var sample = motion.Sample(sampleTime);
            if (!PreviewWindow.PositionWindow(hwnd, sample.Window) || !ApplyRegion(sample.Clip, sample.Diameter)
                || !GetWindowRect(hwnd, out var actual)) return;
            var outer = Rectangle.FromLTRB(actual.Left, actual.Top, actual.Right, actual.Bottom);
            // A DPI change can reposition the HWND during SetWindowPos. Retry on the next composition tick.
            if (outer != sample.Window) return;
            appliedSample = sampleTime;
            var visible = new Rectangle(outer.X + sample.Clip.X, outer.Y + sample.Clip.Y, sample.Clip.Width, sample.Clip.Height);
            FirstDockBounds ??= visible;
            if (sample.Complete)
            {
                LandingBounds = visible;
                motion.Landed = true;
                Stop(clear: false);
            }
            return;
        }
        long start = host.FullscreenEntryStarted;
        // Give a cold renderer time to show its first image, then run the
        // window motion even if the input or AI startup is still waiting.
        if (start == 0 && now - openedAt >= 3_000_000)
            start = host.StartFullscreenEntry(now);
        long elapsed = start == 0 ? 0 : now - start;
        if (elapsed >= FullscreenMotion.Duration) Stop(clear: true);
        else Apply(elapsed);
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
