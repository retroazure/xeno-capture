using System;
using System.Threading;
using XenoCapture.Engine;

namespace XenoCapture.UI;

internal sealed partial class Direct3DHost
{
    sealed partial class RenderLoop
    {
        // The render thread owns the attached preview and its timing state.
        // The host supplies settings, the latest capture, and presentation callbacks.
        readonly Direct3DHost host;
        readonly nint window;

        D3DPreview? preview;
        FrameDeadlineWaiter? deadline;
        nint renderSignal;
        D3DPreview Renderer => preview!; // Draw paths run only with an attached preview.

        FrameGenerationMode previousMode = FrameGenerationMode.Off;
        bool force30Fps;
        bool previousForce30Fps;
        int multiplier = 2;
        string? timingFailure;
        long viewportWaitLimit;
        double refreshHz;
        long refreshChecked;
        int lastCaptureWidth;
        int lastCaptureHeight;
        long lastCapturePeriod;
        long contentFramePeriod;
        long consumedArrival;
        long lastPresentedAt;

        bool entryAnimating;
        bool motionTimerUnavailable;
        long motionRedrawAt;
        PresentedFrame lastMotionFrame;

        readonly record struct CaptureSnapshot(bool Enabled, bool HasFrame, PixelLayout? Layout,
            long Duration, int Width, int Height);

        internal RenderLoop(Direct3DHost host, nint window)
        {
            this.host = host;
            this.window = window;
            entryAnimating = host.animateFullscreenEntry;
        }

        internal void Run()
        {
            Exception? error = null;
            bool notify = false;
            Apartment? apartment = null;
            MultimediaScheduling? scheduling = null;

            Interlocked.Increment(ref liveRenderers);
            try
            {
                scheduling = MultimediaScheduling.Enter("Playback");
                apartment = new Apartment();
                if (host.handoff == null)
                    preview = new D3DPreview(window, Volatile.Read(ref host.pixelWidth), Volatile.Read(ref host.pixelHeight));

                viewportWaitLimit = Clock.Now + 300_000;
                renderSignal = host.signal.SafeWaitHandle.DangerousGetHandle();
                RenderFrames();
            }
            catch (Exception ex)
            {
                error = ex;
                lock (host.gate) notify = !host.stopping;
            }
            finally
            {
                try { ReleaseRenderer(error == null && !host.releaseOnStop); }
                catch (Exception ex)
                {
                    if (error == null)
                    {
                        error = ex;
                        lock (host.gate) notify = !host.stopping;
                    }
                }

                deadline?.Dispose();
                apartment?.Dispose();
                scheduling?.Dispose();
                Complete(error, notify);
            }
        }

        void Complete(Exception? error, bool notify)
        {
            lock (host.gate)
            {
                host.stopping = true;
                host.active = false;
                host.latest?.Dispose();
                host.latest = null;
                host.inactiveWaiter?.TrySetException(error ?? new ObjectDisposedException(nameof(Direct3DHost)));
                host.inactiveWaiter = null;
                host.signal.Dispose();
            }
            if (notify && error != null)
            {
                try { host.Failed?.Invoke(error); }
                catch { } // An observer cannot interrupt teardown.
            }
            Interlocked.Decrement(ref liveRenderers);
            host.completed.TrySetResult();
        }

        void RenderFrames()
        {
            while (TryReadCapture(out var capture))
            {
                var requested = host.FrameGeneration;
                multiplier = FrameGenerationPolicy.Multiplier(requested);
                force30Fps = host.Capped30Game;
                var prediction = new PredictionMode(FrameGenerationPolicy.PredictiveTarget(requested),
                    requested == FrameGenerationMode.RecoverGaps);

                if (!capture.Enabled)
                {
                    Suspend(requested);
                    continue;
                }
                if (Volatile.Read(ref host.fullscreenDock) is { } docking)
                {
                    RenderFullscreenExit(docking);
                    continue;
                }
                if (!EnsureRenderer(requested, capture)) continue;

                Renderer.AiVideo = host.AiVideo;
                PublishAiStatus();
                UpdateFrameGenerationMode(requested);

                bool paceSource = (requested == FrameGenerationMode.Off || prediction.RecoveringGaps) && host.SmoothSourcePacing;
                bool steadySource = host.isFullscreen && paceSource && requested == FrameGenerationMode.Off
                    && host.SteadyFullscreenPacing && !entryAnimating;
                if (!steadySource) steadyCadence = default;
                if ((multiplier > 1 || prediction.Requested || paceSource) && (capture.HasFrame || Renderer.HasInterpolationFrame))
                    UpdateRefreshRate();

                bool generating = SelectInterpolation(capture);
                int activeMultiplier = generating ? multiplier : 0;
                if (host.EffectiveFrameGenerationMultiplier != activeMultiplier)
                    Volatile.Write(ref host.effectiveFrameGenerationMultiplier, activeMultiplier);

                if (generating) RenderInterpolation(requested, capture.HasFrame);
                else RenderSource(requested, capture, prediction, paceSource, steadySource);
            }
        }

        bool TryReadCapture(out CaptureSnapshot capture)
        {
            lock (host.gate)
            {
                if (host.stopping)
                {
                    capture = default;
                    return false;
                }
                bool enabled = host.active;
                var frame = host.latest;
                capture = new(enabled, enabled && frame != null, frame?.Layout, frame?.Duration ?? lastCapturePeriod,
                    frame?.Width ?? lastCaptureWidth, frame?.Height ?? lastCaptureHeight);
                return true;
            }
        }

        void Suspend(FrameGenerationMode requested)
        {
            if (host.handoff == null)
            {
                Renderer.AiVideo = host.AiVideo;
                Renderer.SuspendEnhancements();
                PublishAiStatus();
            }
            else
            {
                if (preview != null)
                {
                    Renderer.AiVideo = host.AiVideo;
                    ReleaseRenderer(preserve: true);
                }
                PublishIdleAiStatus();
            }

            ResetFrameSchedule(requested);
            steadyCadence = default;
            lock (host.gate)
            {
                host.inactiveWaiter?.TrySetResult();
                host.inactiveWaiter = null;
            }
            host.signal.WaitOne();
        }

        void ResetFrameSchedule(FrameGenerationMode requested)
        {
            timingFailure = null;
            previousMode = requested;
            previousForce30Fps = force30Fps;
            cadenceDue = 0;
            interpolationStep = 0;
            Volatile.Write(ref host.frameGenerationActive, false);
            predictionDue = 0;
            predictionShown = false;
            sourcePacing = default;
        }

        void UpdateFrameGenerationMode(FrameGenerationMode requested)
        {
            if (requested == previousMode && force30Fps == previousForce30Fps) return;

            if (Renderer.HasInterpolationFrame || requested != previousMode)
                Renderer.ResetInterpolation();
            ResetFrameSchedule(requested);
            highRateCadence = default;
            limitedMultiplier = 0;
        }

        void UpdateRefreshRate()
        {
            if (Clock.Now - refreshChecked <= 10_000_000 && refreshChecked != 0) return;

            double currentRefresh = host.DiagnosticRefreshRate > 0 ? host.DiagnosticRefreshRate : DisplayTiming.RefreshRate(window);
            if (refreshHz != currentRefresh)
            {
                sourcePacing = default;
                steadyCadence = default;
            }
            refreshHz = currentRefresh;
            refreshChecked = Clock.Now;
        }

        bool EnsureRenderer(FrameGenerationMode requested, in CaptureSnapshot capture)
        {
            if (preview != null) return true;

            // Auto sizes inference to the fullscreen viewport. Avoid
            // presenting at a transient size immediately before DXGI resize.
            if (host.isFullscreen && host.AiVideo is { Enabled: true, OutputHeight: 0 }
                && !(requested == FrameGenerationMode.Off && capture.Layout == PixelLayout.Nv12 && host.FullscreenViewportReady()))
            {
                long readyAt = Math.Min(viewportWaitLimit, Volatile.Read(ref host.viewportChangedAt) + 120_000);
                if (Clock.Now < readyAt)
                {
                    try
                    {
                        deadline ??= new FrameDeadlineWaiter(host.signal);
                        deadline.WaitUntil(readyAt);
                        return false;
                    }
                    catch (System.ComponentModel.Win32Exception) { viewportWaitLimit = 0; }
                }
            }

            var retained = host.handoff?.Take();
            if (retained == null && !capture.HasFrame)
            {
                PublishIdleAiStatus();
                host.signal.WaitOne();
                return false;
            }

            int width = Volatile.Read(ref host.pixelWidth);
            int height = Volatile.Read(ref host.pixelHeight);
            if (retained == null) preview = new D3DPreview(window, width, height);
            else
            {
                preview = retained;
                retained.AttachWindow(window, width, height);
            }
            return true;
        }

        void ReleaseRenderer(bool preserve)
        {
            var owned = preview;
            preview = null;
            if (owned == null) return;
            if (preserve && host.handoff != null)
            {
                try
                {
                    if (host.fullscreenDock is { Landed: true } motion)
                        motion.Surface = owned.RetainPresentationSurface();
                    owned.DetachWindow();
                    host.handoff.Park(owned);
                    return;
                }
                catch { owned.Dispose(); throw; }
            }
            owned.Dispose();
        }

        bool IsActive()
        {
            lock (host.gate) return !host.stopping && host.active;
        }

        bool WaitForFrameSlot(long expires = 0)
        {
            uint timeoutMs = expires == 0 ? 20u : (uint)Math.Clamp((expires - Clock.Now + 9_999) / 10_000, 0, 20);
            var result = Renderer.WaitForFrameSlot(renderSignal, timeoutMs);
            if (result == FrameSlotWait.TimedOut && expires == 0) Interlocked.Increment(ref host.frameWaitTimeouts);
            return result == FrameSlotWait.Ready;
        }
        Packet? TakeLatestFrame()
        {
            lock (host.gate)
            {
                if (host.stopping || !host.active) return null;
                var packet = host.latest;
                consumedArrival = host.latestArrival;
                host.latest = null;
                return packet;
            }
        }

        Packet? RefreshAfterAiStartup(long previousCreations, long duration)
        {
            if (!host.AiVideo.Enabled || Renderer.VsrFeatureCreations == previousCreations || Renderer.LastUploadDrawMs <= Math.Max(5, duration / 5000.0))
                return null;
            lock (host.gate) { if (host.stopping || !host.active || host.latest == null) return null; }
            Renderer.CompleteAiWork();
            return TakeLatestFrame();
        }

        void Requeue(Packet packet)
        {
            lock (host.gate)
            {
                if (host.stopping || !host.active) return;
                if (host.latest == null)
                {
                    host.latest = packet.Retain();
                    host.latestArrival = consumedArrival;
                }
                host.signal.Set();
            }
        }

        void PrepareDraw(bool updateAi = true)
        {
            if (updateAi) Renderer.AiVideo = host.AiVideo;
            Renderer.Resize(Volatile.Read(ref host.pixelWidth), Volatile.Read(ref host.pixelHeight));
            Renderer.EdgeSmoothing = host.EdgeSmoothing;
            Renderer.RetainFullscreenOutput = host.isFullscreen || host.animateFullscreenEntry;
            if (entryAnimating)
            {
                long started = host.FullscreenEntryStarted;
                Renderer.PresentationScale = FullscreenMotion.Scale(started == 0 ? 0 : Clock.Now - started);
            }
        }
        bool Present(PresentedFrame frame, bool restoreSource = false, uint refreshInterval = 1)
        {
            bool accepted = Renderer.Present(allowBlocking: frame.Kind == PresentationKind.Source || restoreSource,
                syncInterval: host.SmoothSourcePacing ? refreshInterval : 0);
            host.LastSubmissionMs = Renderer.LastUploadDrawMs;
            Volatile.Write(ref host.enhancedUpscalingActive, Renderer.UsedEnhancedUpscaling);
            Volatile.Write(ref host.rtxVideoRequested, Renderer.RtxVideoRequested);
            Volatile.Write(ref host.upscalingFallbackReason, Renderer.UpscalingFallbackReason);
            PublishAiStatus();
            if (!accepted) return false;
            lastPresentedAt = Clock.Now;
            if (!entryAnimating && multiplier == 4 && HighRateCadence.Eligible(contentFramePeriod) && host.FrameGenerationActive)
                highRateCadence.Observe(lastPresentedAt, contentFramePeriod);
            if (frame.Kind != PresentationKind.Repeated) lastMotionFrame = frame;
            if (entryAnimating)
            {
                host.StartFullscreenEntry(lastPresentedAt);
                motionRedrawAt = lastPresentedAt + EntryRedrawInterval;
                entryAnimating = Renderer.PresentationScale < 1;
            }
            if (frame.Kind is PresentationKind.Interpolated or PresentationKind.Predicted)
                Interlocked.Increment(ref host.generatedPresentations);
            if (frame.Kind == PresentationKind.Repeated) Interlocked.Increment(ref host.repeatedPresentations);
            host.FramePresented?.Invoke(frame);
            var displayObserver = host.DiagnosticDisplayFrame;
            if (displayObserver != null && Renderer.TryGetDisplayFrame(out var timing))
                displayObserver(timing.PresentCount, timing.PresentRefreshCount);
            return true;
        }
        void PublishAiStatus()
        {
            Volatile.Write(ref host.vsrFeatureCreations, Renderer.VsrFeatureCreations);
            Volatile.Write(ref host.aiVideoActive, Renderer.AiVideoActive);
            Volatile.Write(ref host.aiVideoStatus, Renderer.AiVideoStatus);
            Volatile.Write(ref host.aiVideoFallbackReason, Renderer.AiVideoFallbackReason);
        }
        void PublishIdleAiStatus()
        {
            Volatile.Write(ref host.aiVideoActive, false);
            Volatile.Write(ref host.aiVideoFallbackReason, null);
            Volatile.Write(ref host.aiVideoStatus, host.AiVideo.Enabled ? "AI Video SR · waiting for video" : "Off");
        }

        void SetFrameGenerationStatus(string text)
        {
            if (host.FrameGenerationStatus != text)
                Volatile.Write(ref host.frameGenerationStatus, text);
        }

        long GetContentPeriod(long capturePeriod) => force30Fps
            ? FrameGenerationPolicy.Content30Period(capturePeriod)
            : host.cadence.Period(capturePeriod);

        bool IsCurrentMode(FrameGenerationMode requested) =>
            host.FrameGeneration == requested && host.Capped30Game == force30Fps;

        void RenderFullscreenExit(FullscreenDockMotion docking)
        {
            long presentedAt = docking.PresentedAt;
            if (preview?.CanRedrawFullscreenMotion != true || (presentedAt != 0 && docking.Sample(presentedAt).Complete))
            {
                host.signal.WaitOne();
                return;
            }
            if (WaitForMotionRedraw() || !WaitForFrameSlot() || !IsActive()) return;

            long sampleTime = Clock.Now;
            PrepareDraw(updateAi: false);
            Renderer.RedrawFullscreenMotion(docking.Sample(sampleTime).Clip);
            if (Present(lastMotionFrame with { Kind = PresentationKind.Repeated })) docking.Presented(sampleTime);
            motionRedrawAt = sampleTime + EntryRedrawInterval;
        }

        bool WaitForMotionRedraw()
        {
            long remaining = motionRedrawAt - Clock.Now;
            if (remaining <= 0) return false;
            if (!motionTimerUnavailable)
            {
                try
                {
                    deadline ??= new FrameDeadlineWaiter(host.signal);
                    deadline.WaitUntil(motionRedrawAt);
                    return true;
                }
                catch (System.ComponentModel.Win32Exception) { motionTimerUnavailable = true; }
            }
            host.signal.WaitOne((int)((remaining + 9_999) / 10_000));
            return true;
        }
    }
}
