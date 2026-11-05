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
internal sealed class Direct3DHost : HwndHost
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
        set { lock (gate) { active = value && !stopping; if (!active) { latest?.Dispose(); latest = null; cadence.Reset(); } if (!stopping) signal.Set(); } }
    }
    public Task SuspendAsync()
    {
        lock(gate)
        {
            active=false;latest?.Dispose();latest=null;cadence.Reset();
            if(stopping)return completed.Task;
            if(renderer==null)return Task.CompletedTask;
            inactiveWaiter??=new(TaskCreationOptions.RunContinuationsAsynchronously);signal.Set();return inactiveWaiter.Task;
        }
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
    }
    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        lock (gate)
        {
            if (stopping) throw new ObjectDisposedException(nameof(Direct3DHost));
            nint window = CreateWindowEx(0, "static", "Xeno Direct3D Preview", 0x56010000, 0, 0, 1, 1, parent.Handle, 0, 0, 0);
            if (window == 0) throw new System.ComponentModel.Win32Exception();
            Volatile.Write(ref viewportChangedAt, Clock.Now);
            renderer = new Thread(() => Run(window)) { IsBackground = true, Name = "Xeno Direct3D video", Priority = ThreadPriority.AboveNormal }; renderer.Start();
            return new HandleRef(this, window);
        }
    }    public void Submit(Packet frame)
    {
        lock (gate)
        {
            if (stopping || !active || fullscreenDock != null) return;
            if (FrameGeneration is not (FrameGenerationMode.Off or FrameGenerationMode.RecoverGaps)) cadence.Observe(frame);
            bool wake = latest == null || waitingForSourcePacing;
            latest?.Dispose(); latest = frame.Retain(); latestArrival = Clock.Now;
            if (wake) signal.Set();
        }
    }
    void Run(nint window)
    {
        Exception? error = null; bool notify = false; FrameDeadlineWaiter? deadline = null;
        D3DPreview gpu = null!;
        Apartment? apartment = null;
        MultimediaScheduling? scheduling = null;
        void ReleaseRenderer(bool preserve)
        {
            var owned = gpu;
            gpu = null!;
            if (owned == null) return;
            if (preserve && handoff != null)
            {
                try
                {
                    if (fullscreenDock is { Landed: true } motion)
                        motion.Surface = owned.RetainPresentationSurface();
                    owned.DetachWindow();
                    handoff.Park(owned);
                    return;
                }
                catch { owned.Dispose(); throw; }
            }
            owned.Dispose();
        }
        Interlocked.Increment(ref liveRenderers);
        try
        {
            scheduling = MultimediaScheduling.Enter("Playback"); apartment = new Apartment();
            if (handoff == null) gpu = new D3DPreview(window, Volatile.Read(ref pixelWidth), Volatile.Read(ref pixelHeight));
            FrameGenerationMode previousMode = FrameGenerationMode.Off;
            bool content30=false, previousContent30=false;
            HighRateCadence highRateCadence = default;
            bool limitHighRate = false;
            (double Refresh, long Period, PreviewUpscaler Upscaler, AiVideoSettings Ai) highRateSettings = default;
            long cadenceEpoch=0,cadenceDue=0,cadenceInputAt=0,cadenceScheduledPeriod=0,consumedArrival=0;
            int cadencePhase=0,cadenceStep=0;
            long viewportWaitLimit = Clock.Now + 300_000;
            long cadenceLead=15_000, lastPresentedAt=0;
            long lastGeneratedPair = 0;
            long predictionDue = 0, predictionEnd = 0, predictionArrival = 0, predictionRestoreAt = 0;
            bool predictionStarted = false, predictionShown = false;
            bool? predictionReady = null;
            double predictionDrawMs = 0;
            long entryRedrawAt = 0;
            PresentedFrame lastMotionFrame = default;
            bool entryAnimating = animateFullscreenEntry;
            bool entryTimerUnavailable=false;
            bool sourcePacingTimerUnavailable = false;
            SourceFramePacing sourcePacing = default;
            nint renderSignal = signal.SafeWaitHandle.DangerousGetHandle();
            long period = 0, sourceCapturePeriod=0, refreshChecked = 0;
            int sourceWidth = 0, sourceHeight = 0, multiplier = 2; double refreshHz = 0; string? timingFailure = null;
            bool Alive() { lock (gate) return !stopping && active; }
            bool Slot(long expires = 0)
            {
                uint timeoutMs = expires == 0 ? 20u : (uint)Math.Clamp((expires - Clock.Now + 9_999) / 10_000, 0, 20);
                var result = gpu.WaitForFrameSlot(renderSignal, timeoutMs);
                if (result == FrameSlotWait.TimedOut && expires == 0) Interlocked.Increment(ref frameWaitTimeouts);
                return result == FrameSlotWait.Ready;
            }
            Packet? TakeLatest()
            {
                lock (gate)
                {
                    if (stopping || !active) return null;
                    var packet = latest;consumedArrival=latestArrival; latest = null; return packet;
                }
            }
            bool WaitForSourcePacing()
            {
                if (entryAnimating || sourcePacingTimerUnavailable) return false;
                long due;
                lock (gate)
                {
                    if (stopping || !active || !smoothSourcePacing || latest is not { } frame) return false;
                    due = sourcePacing.Deadline(frame.Time, frame.Duration, latestArrival, refreshHz);
                    if (due <= Clock.Now) return false;
                    waitingForSourcePacing = true;
                }
                try
                {
                    deadline ??= new FrameDeadlineWaiter(signal);
                    Interlocked.Increment(ref sourcePacingWaits);
                    deadline.WaitUntil(due);
                    return true;
                }
                catch (System.ComponentModel.Win32Exception) { sourcePacingTimerUnavailable = true; return false; }
                finally { lock (gate) waitingForSourcePacing = false; }
            }
            Packet? RefreshAfterAiStartup(long previousCreations, long duration)
            {
                if (!AiVideo.Enabled || gpu.VsrFeatureCreations == previousCreations || gpu.LastUploadDrawMs <= Math.Max(5, duration / 5000.0))
                    return null;
                lock (gate) { if (stopping || !active || latest == null) return null; }
                gpu.CompleteAiWork();
                return TakeLatest();
            }
            void Requeue(Packet packet)
            {
                lock (gate) { if (!stopping && active) { if(latest==null){latest=packet.Retain();latestArrival=consumedArrival;} signal.Set(); } }
            }
            void PrepareDraw(bool updateAi = true)
            {
                if (updateAi) gpu.AiVideo = AiVideo;
                gpu.Resize(Volatile.Read(ref pixelWidth), Volatile.Read(ref pixelHeight));
                gpu.EdgeSmoothing = EdgeSmoothing;
                gpu.RetainFullscreenOutput = isFullscreen || animateFullscreenEntry;
                if (entryAnimating)
                {
                    long started = FullscreenEntryStarted;
                    gpu.PresentationScale = FullscreenMotion.Scale(started == 0 ? 0 : Clock.Now - started);
                }
            }
            bool Publish(PresentedFrame frame, bool restoreSource = false)
            {
                bool accepted = gpu.Present(allowBlocking: frame.Kind == PresentationKind.Source || restoreSource);
                LastSubmissionMs = gpu.LastUploadDrawMs;
                Volatile.Write(ref enhancedUpscalingActive, gpu.UsedEnhancedUpscaling); Volatile.Write(ref rtxVideoRequested,gpu.RtxVideoRequested); Volatile.Write(ref upscalingFallbackReason, gpu.UpscalingFallbackReason);
                PublishAiStatus();
                if (!accepted) return false;
                lastPresentedAt = Clock.Now;
                if (!entryAnimating && multiplier == 4 && HighRateCadence.Eligible(period) && FrameGenerationActive)
                    highRateCadence.Observe(lastPresentedAt, period);
                if (frame.Kind != PresentationKind.Repeated) lastMotionFrame = frame;
                if (entryAnimating)
                {
                    StartFullscreenEntry(lastPresentedAt);
                    entryRedrawAt = lastPresentedAt + EntryRedrawInterval;
                    entryAnimating = gpu.PresentationScale < 1;
                }
                if (frame.Kind is PresentationKind.Interpolated or PresentationKind.Predicted)
                    Interlocked.Increment(ref generatedPresentations);
                if (frame.Kind == PresentationKind.Repeated) Interlocked.Increment(ref repeatedPresentations);
                FramePresented?.Invoke(frame);
                return true;
            }
            void PublishAiStatus()
            {
                Volatile.Write(ref vsrFeatureCreations, gpu.VsrFeatureCreations);
                Volatile.Write(ref aiVideoActive, gpu.AiVideoActive);
                Volatile.Write(ref aiVideoStatus, gpu.AiVideoStatus);
                Volatile.Write(ref aiVideoFallbackReason, gpu.AiVideoFallbackReason);
            }
            void PublishIdleAiStatus()
            {
                Volatile.Write(ref aiVideoActive, false);
                Volatile.Write(ref aiVideoFallbackReason, null);
                Volatile.Write(ref aiVideoStatus, AiVideo.Enabled ? "AI Video SR · waiting for video" : "Off");
            }
            void Status(string text) { if (FrameGenerationStatus != text) Volatile.Write(ref frameGenerationStatus, text); }
            long ContentPeriod(long capture)=>content30 ? FrameGenerationPolicy.Content30Period(capture):cadence.Period(capture);
            bool CanShowPrediction(FrameGenerationMode requested)
            {
                lock (gate)
                    return !stopping && active && latest == null && FrameGeneration == requested && Capped30Game == content30;
            }
            bool ShowPrediction(FrameGenerationMode requested, long interval, bool recoveringGaps)
            {
                if (predictionDue == 0 || predictionDue > predictionEnd || predictionReady == false)
                {
                    if (!predictionShown) return false;
                    if (Clock.Now < predictionRestoreAt) { deadline!.WaitUntil(predictionRestoreAt); return true; }
                    if (!Slot() || !CanShowPrediction(requested)) return true;
                    // If the source stalls, settle on the last real image.
                    PrepareDraw();
                    gpu.RenderCachedFrame(Sharpness, Upscaler);
                    if (CanShowPrediction(requested))
                    {
                        Publish(gpu.CachedFrame with { Kind = PresentationKind.Repeated }, restoreSource: true);
                        predictionShown = false;
                    }
                    return true;
                }

                if (!CanShowPrediction(requested)) return true;
                long due = predictionDue;
                long expires = Math.Min(predictionEnd, due + interval / 2);
                long now = Clock.Now;
                if (now > expires) { predictionDue += interval; return true; }
                // Gap recovery leaves the GPU alone during normal source cadence.
                long analysisDue = predictionArrival + interval;
                if (recoveringGaps && now < analysisDue) { deadline!.WaitUntil(analysisDue); return true; }
                if (!predictionStarted)
                {
                    predictionStarted = true;
                    if (!gpu.BeginPrediction()) { predictionReady = false; return true; }
                }
                long lead = recoveringGaps ? 0
                    : Math.Clamp((long)(predictionDrawMs * 10_000) + 2_500, 2_500, Math.Min(15_000, interval / 4));
                if (now < due - lead) { deadline!.WaitUntil(due - lead); return true; }
                predictionReady ??= gpu.PredictionReady();
                long retryAt = recoveringGaps ? Math.Min(expires, due + 20_000) : due;
                if (predictionReady == null && Clock.Now < retryAt) { deadline!.WaitUntil(retryAt); return true; }
                predictionDue += interval;
                if (predictionReady != true || !Slot(expires) || Clock.Now > expires || !CanShowPrediction(requested)) return true;

                PrepareDraw();
                if (Clock.Now > expires || !CanShowPrediction(requested)) return true;
                var frame = gpu.RenderPrediction(due - predictionArrival, interval, Sharpness, Upscaler);
                predictionDrawMs = gpu.LastUploadDrawMs;
                while (Clock.Now < due && CanShowPrediction(requested)) deadline!.WaitUntil(due);
                if (Clock.Now <= expires && CanShowPrediction(requested))
                    predictionShown |= Publish(frame);
                return true;
            }
            bool Prepare(Packet packet)
            {
                sourceWidth = packet.Width; sourceHeight = packet.Height; sourceCapturePeriod=packet.Duration; period = ContentPeriod(packet.Duration);
                if (!gpu.PrepareInterpolation(packet,period,true))
                {
                    Volatile.Write(ref frameGenerationActive, false);
                    Status(gpu.FrameGenerationFailure ?? "Unavailable"); Requeue(packet); return false;
                }
                if (gpu.FrameWasDuplicate) Interlocked.Increment(ref skippedCaptureDuplicates);
                Status($"{(content30 ? "30 FPS game · " : "")}{multiplier}× · {(limitHighRate ? "4× limited by presentation · " : "")}{10_000_000.0 / period:0}→{10_000_000.0 * multiplier / period:0} fps · {gpu.FrameGenerationDetail}");
                Volatile.Write(ref frameGenerationActive, true); return true;
            }
            void SetCadence(long epoch)
            {
                cadenceEpoch = epoch;
                cadencePhase = 1;
                cadenceScheduledPeriod = period;
                cadenceDue = FrameGenerationPolicy.PhaseTime(cadenceEpoch, period, cadencePhase, multiplier);
            }
            void AdvanceCadence(int phases)
            {
                int position = cadencePhase - 1 + phases;
                cadenceEpoch += (position / multiplier) * period;
                cadencePhase = position % multiplier + 1;
                cadenceDue = FrameGenerationPolicy.PhaseTime(cadenceEpoch, period, cadencePhase, multiplier);
            }
            void AlignPair()
            {
                long measuredLead = FrameGenerationPolicy.CadenceLead(period, multiplier, gpu.FrameAnalysisMs);
                cadenceLead = measuredLead >= cadenceLead ? measuredLead : cadenceLead + (measuredLead - cadenceLead) / 4;
                long interval = period / multiplier;
                long desired = consumedArrival + cadenceLead;
                if (cadenceScheduledPeriod != period || Math.Abs(desired - cadenceDue) > interval / 2)
                {
                    // Rejoin a changed cadence or a missed input boundary
                    // while giving the last displayed frame a usable interval.
                    long firstDue = Math.Max(desired, lastPresentedAt + interval * 3 / 4);
                    SetCadence(firstDue - interval);
                    return;
                }
                long correction = FrameGenerationPolicy.GameCadenceCorrection(desired, cadenceDue, period);
                cadenceEpoch += correction;
                cadenceDue += correction;
            }
            while (true)
            {
                bool enabled, pending;
                long candidatePeriod;
                int candidateWidth, candidateHeight;
                lock (gate)
                {
                    if (stopping) break; enabled = active; pending = enabled && latest != null;
                    candidatePeriod = latest?.Duration ?? sourceCapturePeriod; candidateWidth = latest?.Width ?? sourceWidth; candidateHeight = latest?.Height ?? sourceHeight;
                }
                var requested = FrameGeneration; multiplier = FrameGenerationPolicy.Multiplier(requested); content30=Capped30Game;
                int predictiveTarget = FrameGenerationPolicy.PredictiveTarget(requested);
                bool recoveringGaps = requested == FrameGenerationMode.RecoverGaps;
                bool predictionRequested = predictiveTarget > 0 || recoveringGaps;
                if(!enabled)
                {
                    if (handoff == null) { gpu!.AiVideo = AiVideo; gpu.SuspendEnhancements(); PublishAiStatus(); }
                    else
                    {
                        if (gpu != null) { gpu.AiVideo = AiVideo; ReleaseRenderer(true); }
                        PublishIdleAiStatus();
                    }
                    timingFailure=null;previousMode=requested;previousContent30=content30;cadenceDue=0;cadenceStep=0;Volatile.Write(ref frameGenerationActive,false);
                    predictionDue = 0; predictionShown = false;
                    sourcePacing = default;
                    lock(gate){inactiveWaiter?.TrySetResult();inactiveWaiter=null;}
                    signal.WaitOne();continue;
                }
                if (Volatile.Read(ref fullscreenDock) is { } docking)
                {
                    long presentedAt = docking.PresentedAt;
                    if (gpu?.CanRedrawFullscreenMotion != true || (presentedAt != 0 && docking.Sample(presentedAt).Complete))
                    {
                        signal.WaitOne();
                        continue;
                    }
                    long remaining = entryRedrawAt - Clock.Now;
                    if (remaining > 0) { signal.WaitOne((int)((remaining + 9_999) / 10_000)); continue; }
                    if (!Slot() || !Alive()) continue;
                    long sampleTime = Clock.Now;
                    PrepareDraw(updateAi: false);
                    gpu.RedrawFullscreenMotion(docking.Sample(sampleTime).Clip);
                    if (Publish(lastMotionFrame with { Kind = PresentationKind.Repeated })) docking.Presented(sampleTime);
                    entryRedrawAt = Clock.Now + EntryRedrawInterval;
                    continue;
                }
                if (gpu == null)
                {
                    // Auto sizes inference to the fullscreen viewport. Avoid
                    // presenting at a transient size immediately before DXGI resize.
                    if (isFullscreen && AiVideo is { Enabled: true, OutputHeight: 0 })
                    {
                        long readyAt = Math.Min(viewportWaitLimit, Volatile.Read(ref viewportChangedAt) + 120_000);
                        if (Clock.Now < readyAt)
                        {
                            try
                            {
                                deadline ??= new FrameDeadlineWaiter(signal);
                                deadline.WaitUntil(readyAt);
                                continue;
                            }
                            catch (System.ComponentModel.Win32Exception) { viewportWaitLimit = 0; }
                        }
                    }
                    var retained = handoff?.Take();
                    if (retained == null && !pending) { PublishIdleAiStatus(); signal.WaitOne(); continue; }
                    int w = Volatile.Read(ref pixelWidth), h = Volatile.Read(ref pixelHeight);
                    if (retained == null) gpu = new D3DPreview(window, w, h);
                    else { gpu = retained; gpu.AttachWindow(window, w, h); }
                }
                gpu.AiVideo = AiVideo; PublishAiStatus();
                if(requested!=previousMode || content30!=previousContent30)
                {
                    if(gpu.HasInterpolationFrame || requested!=previousMode)gpu.ResetInterpolation();
                    timingFailure=null;previousMode=requested;previousContent30=content30;cadenceDue=0;cadenceStep=0;Volatile.Write(ref frameGenerationActive,false);
                    highRateCadence = default;
                    limitHighRate = false;
                    predictionDue = 0; predictionShown = false;
                    sourcePacing = default;
                }
                bool generating = false;
                bool paceSource = (requested == FrameGenerationMode.Off || recoveringGaps) && SmoothSourcePacing;
                if ((multiplier > 1 || predictionRequested || paceSource) && (pending || gpu.HasInterpolationFrame))
                {
                    if (Clock.Now - refreshChecked > 10_000_000 || refreshChecked == 0)
                    { refreshHz = DiagnosticRefreshRate > 0 ? DiagnosticRefreshRate : DisplayTiming.RefreshRate(window); refreshChecked = Clock.Now; }
                }
                if (multiplier > 1 && (pending || gpu.HasInterpolationFrame))
                {
                    long contentPeriod = ContentPeriod(candidatePeriod);
                    generating = timingFailure == null && gpu.FrameGenerationFailure == null && FrameGenerationPolicy.CanGenerate(contentPeriod, refreshHz, candidateWidth, candidateHeight, multiplier);
                    if (cadenceStep == 0)
                    {
                        var settings = (refreshHz, contentPeriod, Upscaler, AiVideo);
                        if (settings != highRateSettings || !generating || multiplier != 4 || !HighRateCadence.Eligible(contentPeriod))
                        {
                            highRateCadence = default;
                            if (limitHighRate) cadenceScheduledPeriod = 0;
                            limitHighRate = false;
                            highRateSettings = settings;
                        }
                        else if (!limitHighRate && highRateCadence.PreferThreePhases)
                        {
                            limitHighRate = true;
                            cadenceScheduledPeriod = 0;
                        }
                    }
                    if (generating && limitHighRate) multiplier = 3;
                    if (!generating)
                    {
                        string reason = timingFailure ?? gpu.FrameGenerationFailure ?? ((long)candidateWidth * candidateHeight > 1920L * 1080 ? "1080p input limit"
                            : candidatePeriod <= 0 ? "Waiting for source timing" : refreshHz < 2 ? "Display refresh rate unavailable"
                            : $"Needs {10_000_000.0 * multiplier / (ContentPeriod(candidatePeriod)):0} Hz · screen {refreshHz:0} Hz");
                        Status(reason);
                    }
                }
                int activeMultiplier = generating ? multiplier : 0;
                if (EffectiveFrameGenerationMultiplier != activeMultiplier)
                    Volatile.Write(ref effectiveFrameGenerationMultiplier, activeMultiplier);
                if (!generating)
                {
                    long PredictionInterval(long contentPeriod) => recoveringGaps
                        ? (SourceFramePacing.HasRefreshHeadroom(refreshHz, contentPeriod) ? contentPeriod : 0)
                        : FrameGenerationPolicy.PredictionInterval(predictiveTarget, refreshHz, contentPeriod);
                    long predictionInterval = predictionRequested
                        ? PredictionInterval(recoveringGaps ? candidatePeriod : ContentPeriod(candidatePeriod)) : 0;
                    bool predicting = predictionInterval > 0 && (long)candidateWidth * candidateHeight <= 1920L * 1080
                        && gpu.FrameGenerationFailure == null && timingFailure == null;
                    if (predicting && deadline == null)
                    {
                        try { deadline = new FrameDeadlineWaiter(signal); }
                        catch (System.ComponentModel.Win32Exception) { timingFailure = "Accurate frame timing unavailable"; predicting = false; }
                    }
                    if (!predicting)
                    {
                        if (gpu.HasInterpolationFrame) gpu.ResetInterpolation();
                        predictionDue = 0; predictionShown = false;
                        Volatile.Write(ref frameGenerationActive, false);
                    }
                    cadenceDue=0; cadenceStep=0;
                    if (requested == FrameGenerationMode.Off) Status("Off");
                    else if (predictionRequested && (pending || candidatePeriod > 0))
                        Status(timingFailure ?? gpu.FrameGenerationFailure ?? ((long)candidateWidth * candidateHeight > 1920L * 1080
                            ? "Motion prediction supports up to 1080p input · showing source"
                            : refreshHz <= 1 ? "Display refresh rate unavailable · showing source"
                            : candidatePeriod <= 0 ? "Waiting for source timing"
                            : !predicting ? recoveringGaps ? "Gap recovery needs more display refresh headroom" : "Showing source at its native cadence"
                            : recoveringGaps ? "Gap recovery · late captures only · no frame buffering"
                            : $"{Math.Min(predictiveTarget, refreshHz):0} fps target · no frame buffering"));
                    if (!pending)
                    {
                        if (predicting && ShowPrediction(requested, predictionInterval, recoveringGaps)) continue;
                        if (entryAnimating && FullscreenEntryStarted != 0 && gpu.CanRedrawFullscreenMotion)
                        {
                            long remaining = entryRedrawAt - Clock.Now;
                            if (remaining > 0)
                            {
                                if (!entryTimerUnavailable)
                                {
                                    try
                                    {
                                        deadline ??= new FrameDeadlineWaiter(signal);
                                        deadline.WaitUntil(entryRedrawAt);
                                        continue;
                                    }
                                    catch (System.ComponentModel.Win32Exception) { entryTimerUnavailable = true; }
                                }
                                signal.WaitOne((int)((remaining + 9_999) / 10_000));
                                continue;
                            }
                            if (!Slot() || !Alive()) continue;
                            lock (gate) { if (latest != null) continue; }
                            // New captures wake the wait and keep priority. Between them,
                            // move the prepared image without uploading or upscaling it again.
                            PrepareDraw(updateAi: false);
                            gpu.RedrawFullscreenMotion();
                            lock (gate) { if (latest != null) continue; }
                            if (!Publish(lastMotionFrame with { Kind = PresentationKind.Repeated }))
                                entryRedrawAt = Clock.Now + EntryRedrawInterval;
                            continue;
                        }
                        signal.WaitOne(); lock (gate) { if (!stopping && active && latest == null) Interlocked.Increment(ref emptyWakeups); }
                        continue;
                    }
                    if (!Slot() || !Alive() || FrameGeneration != requested || Capped30Game != content30) continue;
                    // ResizeBuffers can stall. Keep the capture slot replaceable until it completes.
                    PrepareDraw();
                    if (paceSource && WaitForSourcePacing()) continue;
                    DiagnosticBeforeFrameLatch?.Invoke();
                    using var frame = TakeLatest();
                    if (frame == null) { Interlocked.Increment(ref skippedAfterWait); continue; }
                    long featureCreations = gpu.VsrFeatureCreations;
                    bool DrawLiveSource(Packet source, bool allowRepeatSkip)
                    {
                        if (predictionRequested)
                        {
                            sourceWidth = source.Width; sourceHeight = source.Height; sourceCapturePeriod = source.Duration;
                            period = recoveringGaps ? source.Duration : ContentPeriod(source.Duration);
                            predictionInterval = PredictionInterval(period);
                            if (predicting && predictionInterval > 0 && (long)source.Width * source.Height <= 1920L * 1080
                                && gpu.CachePredictionSource(source, period, allowRepeatSkip && period > source.Duration * 3 / 2))
                            {
                                if (gpu.FrameWasDuplicate) { Interlocked.Increment(ref skippedCaptureDuplicates); return false; }
                                gpu.RenderCachedFrame(Sharpness, Upscaler);
                                return true;
                            }
                            if (gpu.HasInterpolationFrame) gpu.ResetInterpolation();
                        }
                        gpu.Draw(source, Sharpness, Upscaler);
                        return true;
                    }
                    if (!DrawLiveSource(frame, allowRepeatSkip: !entryAnimating)) continue;
                    predictionDue = 0; predictionShown = false;
                    DiagnosticAfterSourceDraw?.Invoke();
                    // Refresh at most once after a long draw so bursts cannot starve presentation.
                    using var fresh = RefreshAfterAiStartup(featureCreations, frame.Duration)
                        ?? (Clock.Now - consumedArrival >= Math.Max(50_000, frame.Duration) ? TakeLatest() : null);
                    if (fresh != null) DrawLiveSource(fresh, allowRepeatSkip: false);
                    if (!Alive()) continue;
                    var shown = fresh ?? frame;
                    bool accepted = Publish(new PresentedFrame(shown.Time, shown.Duration));
                    if (accepted && (requested == FrameGenerationMode.Off || recoveringGaps))
                        sourcePacing = new(shown.Time, shown.Duration, consumedArrival);
                    if (predicting)
                    {
                        bool hasPair = accepted && gpu.HasInterpolationPair;
                        Volatile.Write(ref frameGenerationActive, hasPair);
                        if (hasPair)
                        {
                            predictionArrival = consumedArrival;
                            if (recoveringGaps)
                            {
                                predictionDue = Math.Max(lastPresentedAt + period / 2,
                                    consumedArrival + period + FrameGenerationPolicy.GapRecoveryGrace(period));
                                predictionEnd = consumedArrival + FrameGenerationPolicy.GapRecoveryExpiry(period);
                                predictionRestoreAt = consumedArrival + period * 3;
                            }
                            else
                            {
                                predictionDue = lastPresentedAt + predictionInterval;
                                predictionEnd = consumedArrival + period - predictionInterval / 4;
                                predictionRestoreAt = consumedArrival + period + predictionInterval / 2;
                            }
                            predictionStarted = false; predictionReady = null;
                        }
                    }
                    continue;
                }
                try { deadline ??= new FrameDeadlineWaiter(signal); } catch (System.ComponentModel.Win32Exception) { timingFailure = "Accurate frame timing unavailable"; continue; }
                { // Pace distinct game frames for both automatic and forced-30 cadence.
                    if (!gpu.HasInterpolationFrame)
                    {
                        if (!pending) { signal.WaitOne(); continue; }
                        if (!Slot() || !Alive() || FrameGeneration!=requested || Capped30Game!=content30) continue;
                        using var first=TakeLatest(); if (first==null || !Prepare(first)) continue;
                        PrepareDraw();
                        long featureCreations = gpu.VsrFeatureCreations;
                        gpu.RenderCachedFrame(Sharpness, Upscaler);
                        using var fresh = RefreshAfterAiStartup(featureCreations, period);
                        if (fresh != null && Prepare(fresh)) gpu.RenderCachedFrame(Sharpness, Upscaler);
                        Publish(gpu.CachedFrame);
                        cadenceLead = FrameGenerationPolicy.CadenceLead(period, multiplier, gpu.FrameAnalysisMs);
                        SetCadence(Math.Max(Clock.Now, consumedArrival + cadenceLead));
                        cadenceStep=0; cadenceInputAt=Clock.Now; continue;
                    }
                    // The two-image history must not be overwritten until every
                    // intermediate and the current real frame have been displayed.
                    if (pending && cadenceStep==0)
                    {
                        using var next=TakeLatest();
                        if (next!=null)
                        {
                            if (!Prepare(next)) continue;
                            cadenceInputAt=Clock.Now;
                            if (!gpu.FrameWasDuplicate)
                            {
                                cadenceStep=gpu.HasInterpolationPair ? 1:multiplier;
                                AlignPair();
                            }
                        }
                    }
                    if (!pending && Clock.Now-cadenceInputAt>Math.Max(period*3,1_000_000))
                    {
                        gpu.ResetInterpolation(); cadenceDue=0; cadenceStep=0; Volatile.Write(ref frameGenerationActive,false);
                        highRateCadence = default;
                        limitHighRate = false;
                        Status("Waiting for video"); signal.WaitOne(); continue;
                    }
                    if (cadenceScheduledPeriod != period) SetCadence(Clock.Now);
                    // Submit the GPU work just ahead of the presentation
                    // deadline, so its variable cost does not shift the phase.
                    long interval = period / multiplier;
                    long lead = cadenceStep == 0 ? 0 : Math.Clamp((long)(gpu.LastUploadDrawMs * 10_000) + 2_500,
                        2_500, Math.Min(15_000, interval / 4));
                    long renderDue = cadenceDue - lead;
                    if (Clock.Now < renderDue) { deadline.WaitUntil(renderDue); continue; }
                    if (!Slot() || !Alive() || FrameGeneration != requested || Capped30Game != content30) continue;
                    if (cadenceStep == 0)
                    {
                        lock (gate) { if (latest != null) continue; }
                    }
                    long now = Clock.Now;
                    int missed = FrameGenerationPolicy.MissedPhases(now, cadenceDue, period, multiplier);
                    if (missed >= multiplier)
                    {
                        // A full content interval was lost. Show the newest
                        // capture and start a fresh phase sequence.
                        using var fresh = TakeLatest();
                        if (fresh != null)
                        {
                            if (!Prepare(fresh)) continue;
                            cadenceInputAt = consumedArrival;
                        }
                        SetCadence(now - period / multiplier);
                        cadenceStep = multiplier;
                    }
                    else if (missed > 0)
                    {
                        if (cadenceStep > 0) cadenceStep = Math.Min(multiplier, cadenceStep + missed);
                        AdvanceCadence(missed);
                        if (now < cadenceDue) continue;
                    }
                    PrepareDraw();
                    bool intermediate = cadenceStep > 0 && cadenceStep < multiplier;
                    long pair = gpu.InterpolationPairRevision;
                    PresentedFrame shown;
                    if (intermediate)
                        shown = gpu.RenderIntermediate(cadenceStep, multiplier, Sharpness, Upscaler);
                    else
                    {
                        gpu.RenderCachedFrame(Sharpness, Upscaler);
                        shown = cadenceStep == multiplier ? gpu.CachedFrame : gpu.CachedFrame with { Kind = PresentationKind.Repeated };
                    }
                    while (Clock.Now < cadenceDue && Alive() && FrameGeneration == requested && Capped30Game == content30)
                        deadline.WaitUntil(cadenceDue);
                    if (!Alive() || FrameGeneration != requested || Capped30Game != content30) continue;
                    if (intermediate && FrameGenerationPolicy.MissedPhases(Clock.Now, cadenceDue, period, multiplier) > 0) continue;
                    if (cadenceStep == 0)
                    {
                        lock (gate) { if (latest != null) continue; }
                    }
                    // Do not jump back to the older source after a generated phase has already been shown.
                    if (FrameGenerationPolicy.ShouldPresentPhase(shown.Kind, intermediate, pair, lastGeneratedPair))
                    {
                        if (Publish(shown) && shown.Kind == PresentationKind.Interpolated)
                            lastGeneratedPair = pair;
                    }
                    cadenceStep = intermediate ? cadenceStep + 1 : 0;
                    AdvanceCadence(1); continue;
                }

            }
        }
        catch (Exception ex) { error = ex; lock (gate) notify = !stopping; }
        finally
        {
            try { ReleaseRenderer(error == null && !releaseOnStop); }
            catch (Exception ex) { if (error == null) { error = ex; lock (gate) notify = !stopping; } }
            deadline?.Dispose();
            apartment?.Dispose();
            scheduling?.Dispose();
            lock (gate) { stopping = true; active = false; latest?.Dispose(); latest = null;inactiveWaiter?.TrySetException(error??new ObjectDisposedException(nameof(Direct3DHost)));inactiveWaiter=null; signal.Dispose(); }
            if (notify && error != null) { try { Failed?.Invoke(error); } catch { } }
            Interlocked.Decrement(ref liveRenderers); completed.TrySetResult();
        }
    }
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
            stopping = true; active = false; latest?.Dispose(); latest = null;
            if (renderer == null) { signal.Dispose(); completed.TrySetResult(); }
            else signal.Set();
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
        renderer?.Join(); DestroyWindow(handle.Handle);
    }
}




