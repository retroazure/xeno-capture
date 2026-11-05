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
        set { lock (gate) { if (!stopping) { Volatile.Write(ref generationMode, (int)value); Volatile.Write(ref frameGenerationStatus, value == FrameGenerationMode.Off ? "Off" : "Waiting for video · adds one frame of delay"); signal.Set(); } } }
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
            if(FrameGeneration!=FrameGenerationMode.Off)cadence.Observe(frame);
            latest?.Dispose(); latest = frame.Retain();latestArrival=Clock.Now; signal.Set();
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
            long entryRedrawAt = 0;
            PresentedFrame lastMotionFrame = default;
            bool entryAnimating = animateFullscreenEntry;
            bool entryTimerUnavailable=false;
            nint renderSignal = signal.SafeWaitHandle.DangerousGetHandle();
            long period = 0, sourceCapturePeriod=0, refreshChecked = 0;
            int sourceWidth = 0, sourceHeight = 0, multiplier = 2; double refreshHz = 0; string? timingFailure = null;
            bool Alive() { lock (gate) return !stopping && active; }
            bool Slot()
            {
                var result = gpu.WaitForFrameSlot(renderSignal);
                if (result == FrameSlotWait.TimedOut) Interlocked.Increment(ref frameWaitTimeouts);
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
            bool Publish(PresentedFrame frame)
            {
                bool accepted = gpu.Present(); LastSubmissionMs = gpu.LastUploadDrawMs;
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
                if (frame.Kind == PresentationKind.Interpolated) Interlocked.Increment(ref generatedPresentations);
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
                if(!enabled)
                {
                    if (handoff == null) { gpu!.AiVideo = AiVideo; gpu.SuspendEnhancements(); PublishAiStatus(); }
                    else
                    {
                        if (gpu != null) { gpu.AiVideo = AiVideo; ReleaseRenderer(true); }
                        PublishIdleAiStatus();
                    }
                    timingFailure=null;previousMode=requested;previousContent30=content30;cadenceDue=0;cadenceStep=0;Volatile.Write(ref frameGenerationActive,false);
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
                }
                bool generating = false;
                if (multiplier > 1 && (pending || gpu.HasInterpolationFrame))
                {
                    if (Clock.Now - refreshChecked > 10_000_000 || refreshChecked == 0)
                    { refreshHz = DiagnosticRefreshRate > 0 ? DiagnosticRefreshRate : DisplayTiming.RefreshRate(window); refreshChecked = Clock.Now; }
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
                    if (gpu.HasInterpolationFrame) { gpu.ResetInterpolation(); }
                    cadenceDue=0; cadenceStep=0; Volatile.Write(ref frameGenerationActive, false); if (requested == FrameGenerationMode.Off) Status("Off");
                    if (!pending)
                    {
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
                            if (!Publish(lastMotionFrame with { Kind = PresentationKind.Repeated }))
                                entryRedrawAt = Clock.Now + EntryRedrawInterval;
                            continue;
                        }
                        signal.WaitOne(); lock (gate) { if (!stopping && active && latest == null) Interlocked.Increment(ref emptyWakeups); }
                        continue;
                    }
                    if (!Slot()) continue;
                    DiagnosticBeforeFrameLatch?.Invoke();
                    using var frame = TakeLatest();
                    if (frame == null) { Interlocked.Increment(ref skippedAfterWait); continue; }
                    PrepareDraw();
                    long featureCreations = gpu.VsrFeatureCreations;
                    gpu.Draw(frame, Sharpness, Upscaler);
                    // A slow feature creation can supersede the first frame. Latch after the fence.
                    using var fresh = RefreshAfterAiStartup(featureCreations, frame.Duration);
                    if (fresh != null) gpu.Draw(fresh, Sharpness, Upscaler);
                    var shown = fresh ?? frame;
                    Publish(new PresentedFrame(shown.Time, shown.Duration));
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




