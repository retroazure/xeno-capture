using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using XenoCapture.Engine;

namespace XenoCapture.UI;
internal sealed partial class Direct3DHost : HwndHost
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint CreateWindowEx(uint exStyle, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint module, nint param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] static extern nint SetFocus(nint window);
    readonly object gate = new();
    readonly AutoResetEvent signal = new(false);

    readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Packet? latest;long latestArrival;
    bool waitingForSourcePacing;
    readonly FrameCadenceTracker cadence=new();
    Thread? renderer;TaskCompletionSource? inactiveWaiter;
    bool stopping, active;
    int pixelWidth = 1, pixelHeight = 1;
    long viewportChangedAt;
    ulong fullscreenViewportSize;
    double sharpness;
    int generationMode; string frameGenerationStatus = "Off"; bool frameGenerationActive;
    long generatedPresentations, repeatedPresentations;
    int effectiveFrameGenerationMultiplier;
    internal int EffectiveFrameGenerationMultiplier => Volatile.Read(ref effectiveFrameGenerationMultiplier);
    public FrameGenerationMode FrameGeneration
    {
        get => (FrameGenerationMode)Volatile.Read(ref generationMode);
        set { lock (gate) { if (!stopping) { Volatile.Write(ref generationMode, (int)value); Volatile.Write(ref frameGenerationStatus, FrameGenerationPolicy.WaitingStatus(value)); signal.Set(); } } }
    }
    int capped30Game; long skippedCaptureDuplicates;
    public bool Capped30Game
    {
        get => Volatile.Read(ref capped30Game)!=0;
        set { lock (gate) { if (!stopping) { Volatile.Write(ref capped30Game,value ? 1:0); signal.Set(); } } }
    }
    public long SkippedCaptureDuplicates => Interlocked.Read(ref skippedCaptureDuplicates);
    public string FrameGenerationStatus => Volatile.Read(ref frameGenerationStatus);
    public bool FrameGenerationActive => Volatile.Read(ref frameGenerationActive);
    bool smoothSourcePacing = true;
    long sourcePacingWaits;
    internal long SourcePacingWaits => Interlocked.Read(ref sourcePacingWaits);
    internal bool SmoothSourcePacing
    {
        get => Volatile.Read(ref smoothSourcePacing);
        set { lock (gate) { if (!stopping) { Volatile.Write(ref smoothSourcePacing, value); signal.Set(); } } }
    }
    bool steadyFullscreenPacing;
    internal bool SteadyFullscreenPacing
    {
        get => Volatile.Read(ref steadyFullscreenPacing);
        set { lock (gate) { if (!stopping) { Volatile.Write(ref steadyFullscreenPacing, value); signal.Set(); } } }
    }
    public long GeneratedPresentations => Interlocked.Read(ref generatedPresentations);
    public long RepeatedPresentations => Interlocked.Read(ref repeatedPresentations);
    internal double DiagnosticRefreshRate { get; set; }
    AiVideoSettings aiVideo=AiVideoSettings.Off;bool aiVideoActive;string aiVideoStatus="Off";string? aiVideoFallbackReason;
    public AiVideoSettings AiVideo {get=>Volatile.Read(ref aiVideo);set{lock(gate){if(!stopping){Volatile.Write(ref aiVideo,AiVideoSettings.Validate(value));signal.Set();}}}}
    long vsrFeatureCreations;
    internal long VsrFeatureCreations => Volatile.Read(ref vsrFeatureCreations);
    public bool AiVideoActive=>Volatile.Read(ref aiVideoActive);
    public string AiVideoStatus=>Volatile.Read(ref aiVideoStatus);
    public string? AiVideoFallbackReason=>Volatile.Read(ref aiVideoFallbackReason);
    int upscaler; bool enhancedUpscalingActive, rtxVideoRequested; string? upscalingFallbackReason;
    public PreviewUpscaler Upscaler { get => (PreviewUpscaler)Volatile.Read(ref upscaler); set => Volatile.Write(ref upscaler, (int)value); }
    public bool EnhancedUpscalingActive => Volatile.Read(ref enhancedUpscalingActive);
    public bool RtxVideoRequested => Volatile.Read(ref rtxVideoRequested);
    public string? UpscalingFallbackReason => Volatile.Read(ref upscalingFallbackReason);
    long emptyWakeups, frameWaitTimeouts, skippedAfterWait;
    static int liveRenderers;
    internal static int LiveRenderers => Volatile.Read(ref liveRenderers);
    internal long SkippedAfterWait => Interlocked.Read(ref skippedAfterWait);
    internal Action? DiagnosticBeforeFrameLatch { get; set; }
    internal Action? DiagnosticAfterSourceDraw { get; set; }
    internal event Action<uint, uint>? DiagnosticDisplayFrame;
    internal long EmptyWakeups => Interlocked.Read(ref emptyWakeups);
    internal long FrameWaitTimeouts => Interlocked.Read(ref frameWaitTimeouts);
    internal void DiagnosticWake() { lock (gate) { if (!stopping) signal.Set(); } }
    public event Action<PresentedFrame>? FramePresented;
    public event Action<Exception>? Failed;
    public event Action? ExitRequested;
    public double LastSubmissionMs { get; private set; }
    public bool Active
    {
        get { lock (gate) return active; }
        set
        {
            lock (gate)
            {
                active = value && !stopping;
                if (!active) ClearPendingCapture();
                if (!stopping) signal.Set();
            }
        }
    }
    public Task SuspendAsync()
    {
        lock (gate)
        {
            active = false;
            ClearPendingCapture();
            if (stopping) return completed.Task;
            if (renderer == null) return Task.CompletedTask;

            inactiveWaiter ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            signal.Set();
            return inactiveWaiter.Task;
        }
    }

    // Call while holding the capture gate.
    void ClearPendingCapture()
    {
        latest?.Dispose();
        latest = null;
        cadence.Reset();
    }
    public double Sharpness { get => Volatile.Read(ref sharpness); set => Volatile.Write(ref sharpness, value); }
    readonly PreviewRendererHandoff? handoff;
    readonly bool isFullscreen, animateFullscreenEntry;
    const long EntryRedrawInterval = 83_334; // Up to 120 Hz.
    long fullscreenEntryStarted;
    FullscreenDockMotion? fullscreenDock;
    volatile bool edgeSmoothing;
    internal bool EdgeSmoothing { get => edgeSmoothing; set => edgeSmoothing = value; }
    internal long FullscreenEntryStarted => Volatile.Read(ref fullscreenEntryStarted);
    internal long StartFullscreenEntry(long now)
    {
        long previous = Interlocked.CompareExchange(ref fullscreenEntryStarted, now, 0);
        return previous == 0 ? now : previous;
    }
    internal bool StartFullscreenExit(FullscreenDockMotion motion)
    {
        lock (gate)
        {
            if (stopping || fullscreenDock != null) return false;
            Volatile.Write(ref fullscreenDock, motion);
            signal.Set();
            return true;
        }
    }
    public Direct3DHost(PreviewRendererHandoff? handoff = null, bool isFullscreen = false, bool animateFullscreenEntry = false)
    {
        this.handoff = handoff;
        this.isFullscreen = isFullscreen;
        this.animateFullscreenEntry = animateFullscreenEntry;
        Focusable = true;
        SizeChanged += (_, _) => UpdateSize();
    }
    void UpdateSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = Math.Max(1, (int)Math.Round(ActualWidth * dpi.DpiScaleX));
        int height = Math.Max(1, (int)Math.Round(ActualHeight * dpi.DpiScaleY));
        if (width == pixelWidth && height == pixelHeight) return;
        Volatile.Write(ref pixelWidth, width);
        Volatile.Write(ref pixelHeight, height);
        Volatile.Write(ref viewportChangedAt, Clock.Now);
        WakeWhenFullscreenViewportReady();
    }

    internal void SetFullscreenViewportSize(int width, int height)
    {
        ulong size = Keys.Pair(width, height);
        if (Volatile.Read(ref fullscreenViewportSize) == size) return;
        Volatile.Write(ref fullscreenViewportSize, size);
        WakeWhenFullscreenViewportReady();
    }

    bool FullscreenViewportReady()
    {
        ulong size = Volatile.Read(ref fullscreenViewportSize);
        return size != 0 && size == Keys.Pair(Volatile.Read(ref pixelWidth), Volatile.Read(ref pixelHeight));
    }

    void WakeWhenFullscreenViewportReady()
    {
        if (!isFullscreen || FrameGeneration != FrameGenerationMode.Off ||
            AiVideo is not { Enabled: true, OutputHeight: 0 } || !FullscreenViewportReady()) return;
        lock (gate)
        {
            if (!stopping && active && latest?.Layout == PixelLayout.Nv12) signal.Set();
        }
    }

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        lock (gate)
        {
            if (stopping) throw new ObjectDisposedException(nameof(Direct3DHost));
            const uint previewStyle = 0x56010000, notifyMouse = 0x100; // SS_NOTIFY enables hit testing on STATIC.
            uint style = isFullscreen ? previewStyle | notifyMouse : previewStyle;
            nint window = CreateWindowEx(0, "static", "Xeno Direct3D Preview", style, 0, 0, 1, 1, parent.Handle, 0, 0, 0);
            if (window == 0) throw new System.ComponentModel.Win32Exception();
            Volatile.Write(ref viewportChangedAt, Clock.Now);
            renderer = new Thread(() => Run(window))
            {
                IsBackground = true,
                Name = "Xeno Direct3D video",
                Priority = ThreadPriority.AboveNormal
            };
            renderer.Start();
            return new HandleRef(this, window);
        }
    }

    public void Submit(Packet frame)
    {
        lock (gate)
        {
            if (stopping || !active || fullscreenDock != null) return;
            if (FrameGeneration is not (FrameGenerationMode.Off or FrameGenerationMode.RecoverGaps)) cadence.Observe(frame);
            bool wake = latest == null || waitingForSourcePacing;
            latest?.Dispose();
            latest = frame.Retain();
            latestArrival = Clock.Now;
            if (wake) signal.Set();
        }
    }
    void Run(nint window) => new RenderLoop(this, window).Run();

    bool IsPreviewShortcut(int message, nint key)
    {
        const int keyDown = 0x100, f11 = 122, escape = 27;
        return !stopping && message == keyDown && (key == f11 || (isFullscreen && key == escape));
    }

    protected override nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (IsPreviewShortcut(msg, wParam))
        {
            handled = true;
            Dispatcher.BeginInvoke(() => { if (!stopping) ExitRequested?.Invoke(); });
        }
        const int leftButtonDown = 0x201;
        if (msg == leftButtonDown) SetFocus(hwnd);
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    protected override bool TranslateAcceleratorCore(ref MSG msg, ModifierKeys modifiers)
    {
        if (IsPreviewShortcut(msg.message, msg.wParam))
        {
            ExitRequested?.Invoke();
            return true;
        }
        return base.TranslateAcceleratorCore(ref msg, modifiers);
    }
    volatile bool releaseOnStop;
    void RequestStop(bool releaseRenderer = false)
    {
        lock (gate)
        {
            if (releaseRenderer) releaseOnStop = true;
            if (stopping) return;
            stopping = true;
            active = false;
            latest?.Dispose();
            latest = null;
            if (renderer == null)
            {
                signal.Dispose();
                completed.TrySetResult();
            }
            else
            {
                signal.Set();
            }
        }
    }
    public Task StopAsync(bool releaseRenderer = false)
    {
        RequestStop(releaseRenderer);
        return completed.Task;
    }
    protected override void Dispose(bool disposing) { RequestStop(); base.Dispose(disposing); }
    protected override void DestroyWindowCore(HandleRef handle)
    {
        RequestStop();
        // Keep the HWND alive until its render thread has released the swap chain.
        renderer?.Join();
        DestroyWindow(handle.Handle);
    }
}




