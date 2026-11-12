using System;
using System.Threading;
using XenoCapture.Engine;

namespace XenoCapture.UI;

internal sealed partial class Direct3DHost
{
    sealed partial class RenderLoop
    {
        readonly record struct PredictionMode(int TargetFps, bool RecoveringGaps)
        {
            internal bool Requested => TargetFps > 0 || RecoveringGaps;
        }

        SourceFramePacing sourcePacing;
        FullscreenSourceCadence steadyCadence;
        bool sourcePacingTimerUnavailable;

        long predictionDue;
        long predictionEnd;
        long predictionArrival;
        long predictionRestoreAt;
        bool predictionStarted;
        bool predictionShown;
        bool? predictionReady;
        double predictionDrawMs;

        void RenderSource(FrameGenerationMode requested, in CaptureSnapshot capture, PredictionMode prediction,
            bool paceSource, bool steadySource)
        {
            long predictionInterval = ConfigureSourcePlayback(requested, capture, prediction, out bool predicting);
            if (!capture.HasFrame)
            {
                PresentWithoutCapture(requested, prediction, predictionInterval, predicting);
                return;
            }

            if (!WaitForFrameSlot() || !IsActive() || !IsCurrentMode(requested)) return;
            // ResizeBuffers can stall. Keep the capture slot replaceable until it completes.
            PrepareDraw();
            uint clockInterval = steadySource ? FullscreenSourceCadence.RefreshInterval(capture.Duration, refreshHz) : 0;
            if (clockInterval == 0) steadyCadence = default;
            bool prepareAhead = host.isFullscreen && paceSource && (Renderer.AiVideo.Enabled || clockInterval != 0)
                && !entryAnimating && !sourcePacingTimerUnavailable;
            if (paceSource && !prepareAhead && WaitForSourcePacing()) return;

            host.DiagnosticBeforeFrameLatch?.Invoke();
            using var frame = TakeLatestFrame();
            if (frame == null)
            {
                Interlocked.Increment(ref host.skippedAfterWait);
                return;
            }

            uint sourceInterval = prepareAhead && clockInterval != 0
                ? FullscreenSourceCadence.RefreshInterval(frame.Duration, refreshHz) : 0;
            long sourceDue = 0;
            long steadySubmitBy = 0;
            if (sourceInterval != 0)
                sourceDue = steadyCadence.Deadline(frame.Time, frame.Duration, consumedArrival, refreshHz, out steadySubmitBy);
            else if (prepareAhead)
                sourceDue = SourcePacingDeadline(frame, consumedArrival, LastSourceDisplayTime(), prepareAhead: true);

            long featureCreations = Renderer.VsrFeatureCreations;
            if (!DrawSourceFrame(frame, prediction, predicting, !entryAnimating, ref predictionInterval)) return;
            predictionDue = 0;
            predictionShown = false;
            host.DiagnosticAfterSourceDraw?.Invoke();

            // Refresh at most once after a long draw so bursts cannot starve presentation.
            using var fresh = RefreshAfterAiStartup(featureCreations, frame.Duration)
                ?? (Clock.Now - consumedArrival >= Math.Max(50_000, frame.Duration) ? TakeLatestFrame() : null);
            if (fresh != null) DrawSourceFrame(fresh, prediction, predicting, false, ref predictionInterval);
            else WaitAfterSourceDraw(requested, sourceDue, sourceInterval);
            if (!IsActive()) return;

            var shown = fresh ?? frame;
            // A missed deadline should let the next capture catch up at the next refresh.
            uint refreshInterval = sourceInterval != 0 && fresh == null && host.SteadyFullscreenPacing
                && host.FrameGeneration == requested && Clock.Now < steadySubmitBy
                ? sourceInterval : 1;
            bool accepted = Present(new PresentedFrame(shown.Time, shown.Duration), refreshInterval: refreshInterval);
            if (accepted && (requested == FrameGenerationMode.Off || prediction.RecoveringGaps))
            {
                // Steady cadence tracks the capture clock without a DXGI present counter.
                uint? presentCount = sourceInterval == 0 && host.isFullscreen && paceSource && !entryAnimating
                    && SourceFramePacing.HasRefreshHeadroom(refreshHz, shown.Duration) ? Renderer.LastPresentCount() : null;
                sourcePacing = new(shown.Time, shown.Duration, consumedArrival, presentCount);
            }
            if (predicting) SchedulePrediction(accepted, prediction, predictionInterval);
        }

        long GetPredictionInterval(PredictionMode mode, long contentPeriod) => mode.RecoveringGaps
            ? (SourceFramePacing.HasRefreshHeadroom(refreshHz, contentPeriod) ? contentPeriod : 0)
            : FrameGenerationPolicy.PredictionInterval(mode.TargetFps, refreshHz, contentPeriod);

        long ConfigureSourcePlayback(FrameGenerationMode requested, in CaptureSnapshot capture,
            PredictionMode mode, out bool predicting)
        {
            long interval = mode.Requested
                ? GetPredictionInterval(mode, mode.RecoveringGaps ? capture.Duration : GetContentPeriod(capture.Duration)) : 0;
            predicting = interval > 0 && (long)capture.Width * capture.Height <= 1920L * 1080
                && Renderer.FrameGenerationFailure == null && timingFailure == null;
            if (predicting && deadline == null)
            {
                try { deadline = new FrameDeadlineWaiter(host.signal); }
                catch (System.ComponentModel.Win32Exception)
                {
                    timingFailure = "Accurate frame timing unavailable";
                    predicting = false;
                }
            }
            if (!predicting)
            {
                if (Renderer.HasInterpolationFrame) Renderer.ResetInterpolation();
                predictionDue = 0;
                predictionShown = false;
                Volatile.Write(ref host.frameGenerationActive, false);
            }
            cadenceDue = 0;
            interpolationStep = 0;
            if (requested == FrameGenerationMode.Off) SetFrameGenerationStatus("Off");
            else if (mode.Requested && (capture.HasFrame || capture.Duration > 0))
                SetFrameGenerationStatus(PredictionStatus(capture, mode, predicting));
            return interval;
        }

        string PredictionStatus(in CaptureSnapshot capture, PredictionMode mode, bool predicting)
        {
            if (timingFailure != null) return timingFailure;
            if (Renderer.FrameGenerationFailure is { } failure) return failure;
            if ((long)capture.Width * capture.Height > 1920L * 1080) return "Motion prediction supports up to 1080p input · showing source";
            if (refreshHz <= 1) return "Display refresh rate unavailable · showing source";
            if (capture.Duration <= 0) return "Waiting for source timing";
            if (!predicting)
                return mode.RecoveringGaps ? "Gap recovery needs more display refresh headroom" : "Showing source at its native cadence";
            return mode.RecoveringGaps ? "Gap recovery · late captures only · no frame buffering"
                : $"{Math.Min(mode.TargetFps, refreshHz):0} fps target · no frame buffering";
        }

        void PresentWithoutCapture(FrameGenerationMode requested, PredictionMode prediction, long interval, bool predicting)
        {
            if (predicting && TryPresentPrediction(requested, interval, prediction.RecoveringGaps)) return;
            if (entryAnimating && host.FullscreenEntryStarted != 0 && Renderer.CanRedrawFullscreenMotion)
            {
                if (WaitForMotionRedraw() || !WaitForFrameSlot() || !IsActive()) return;
                lock (host.gate) { if (host.latest != null) return; }
                // New captures wake the wait and keep priority. Between them,
                // move the prepared image without uploading or upscaling it again.
                PrepareDraw(updateAi: false);
                Renderer.RedrawFullscreenMotion();
                lock (host.gate) { if (host.latest != null) return; }
                if (!Present(lastMotionFrame with { Kind = PresentationKind.Repeated }))
                    motionRedrawAt = Clock.Now + EntryRedrawInterval;
                return;
            }

            host.signal.WaitOne();
            lock (host.gate)
                if (!host.stopping && host.active && host.latest == null)
                    Interlocked.Increment(ref host.emptyWakeups);
        }

        bool DrawSourceFrame(Packet source, PredictionMode mode, bool predicting, bool allowRepeatSkip, ref long interval)
        {
            if (mode.Requested)
            {
                lastCaptureWidth = source.Width;
                lastCaptureHeight = source.Height;
                lastCapturePeriod = source.Duration;
                contentFramePeriod = mode.RecoveringGaps ? source.Duration : GetContentPeriod(source.Duration);
                interval = GetPredictionInterval(mode, contentFramePeriod);
                if (predicting && interval > 0 && (long)source.Width * source.Height <= 1920L * 1080
                    && Renderer.CachePredictionSource(source, contentFramePeriod, allowRepeatSkip && contentFramePeriod > source.Duration * 3 / 2))
                {
                    if (Renderer.FrameWasDuplicate)
                    {
                        Interlocked.Increment(ref host.skippedCaptureDuplicates);
                        return false;
                    }
                    Renderer.RenderCachedFrame(host.Sharpness, host.Upscaler);
                    return true;
                }
                if (Renderer.HasInterpolationFrame) Renderer.ResetInterpolation();
            }
            Renderer.Draw(source, host.Sharpness, host.Upscaler);
            return true;
        }

        void WaitAfterSourceDraw(FrameGenerationMode requested, long due, uint sourceInterval)
        {
            if (due <= Clock.Now || !host.SmoothSourcePacing || host.FrameGeneration != requested || !IsActive()) return;

            // Start GPU work during the hold so it can finish before the present deadline.
            Renderer.SubmitPendingDraw();
            while (due > Clock.Now && host.SmoothSourcePacing && host.FrameGeneration == requested
                && (sourceInterval == 0 || host.SteadyFullscreenPacing) && IsActive())
            {
                if (!WaitForSourceDeadline(due)) break;
            }
        }

        void SchedulePrediction(bool accepted, PredictionMode mode, long interval)
        {
            bool hasPair = accepted && Renderer.HasInterpolationPair;
            Volatile.Write(ref host.frameGenerationActive, hasPair);
            if (!hasPair) return;

            predictionArrival = consumedArrival;
            if (mode.RecoveringGaps)
            {
                predictionDue = Math.Max(lastPresentedAt + contentFramePeriod / 2,
                    consumedArrival + contentFramePeriod + FrameGenerationPolicy.GapRecoveryGrace(contentFramePeriod));
                predictionEnd = consumedArrival + contentFramePeriod + FrameGenerationPolicy.GapRecoveryExpiry(contentFramePeriod);
                predictionRestoreAt = consumedArrival + contentFramePeriod * 3;
            }
            else
            {
                predictionDue = lastPresentedAt + interval;
                predictionEnd = consumedArrival + contentFramePeriod - interval / 4;
                predictionRestoreAt = consumedArrival + contentFramePeriod + interval / 2;
            }
            predictionStarted = false;
            predictionReady = null;
        }

        long? LastSourceDisplayTime() => sourcePacing.PresentCount is uint previous
            && Renderer.TryGetDisplayFrame(out var timing) && timing.PresentCount == previous
            ? timing.DisplayedAt(refreshHz) : null;
        long SourcePacingDeadline(Packet frame, long arrival, long? displayedAt, bool prepareAhead = false)
        {
            long? displayDue = displayedAt is long at
                ? sourcePacing.DisplayDeadline(frame.Time, frame.Duration, arrival, refreshHz, at, drawAhead: prepareAhead) : null;
            if (!prepareAhead && displayDue is long due) return due;

            // AI work starts at capture arrival, so cancel early arrival jitter within the
            // existing display hold even when DXGI's estimated phase would release it first.
            long captureDue = sourcePacing.Deadline(frame.Time, frame.Duration, arrival, refreshHz,
                useDisplayBudget: displayDue.HasValue);
            return Math.Max(captureDue, displayDue.GetValueOrDefault());
        }
        bool WaitForSourcePacing()
        {
            if (entryAnimating || sourcePacingTimerUnavailable) return false;
            long? displayedAt = LastSourceDisplayTime();
            long due;
            lock (host.gate)
            {
                if (host.stopping || !host.active || !host.smoothSourcePacing || host.latest is not { } frame) return false;
                due = SourcePacingDeadline(frame, host.latestArrival, displayedAt);
                if (due <= Clock.Now) return false;
                host.waitingForSourcePacing = true;
            }
            try { return WaitForSourceDeadline(due); }
            finally { lock (host.gate) host.waitingForSourcePacing = false; }
        }
        bool WaitForSourceDeadline(long due)
        {
            try
            {
                deadline ??= new FrameDeadlineWaiter(host.signal);
                Interlocked.Increment(ref host.sourcePacingWaits);
                deadline.WaitUntil(due);
                return true;
            }
            catch (System.ComponentModel.Win32Exception) { sourcePacingTimerUnavailable = true; return false; }
        }

        bool CanShowPrediction(FrameGenerationMode requested)
        {
            lock (host.gate)
                return !host.stopping && host.active && host.latest == null && IsCurrentMode(requested);
        }
        bool TryPresentPrediction(FrameGenerationMode requested, long interval, bool recoveringGaps)
        {
            if (predictionDue == 0 || predictionDue > predictionEnd || predictionReady == false)
            {
                if (!predictionShown) return false;
                if (Clock.Now < predictionRestoreAt) { deadline!.WaitUntil(predictionRestoreAt); return true; }
                if (!WaitForFrameSlot() || !CanShowPrediction(requested)) return true;
                // If the source stalls, settle on the last real image.
                PrepareDraw();
                Renderer.RenderCachedFrame(host.Sharpness, host.Upscaler);
                if (CanShowPrediction(requested))
                {
                    Present(Renderer.CachedFrame with { Kind = PresentationKind.Repeated }, restoreSource: true);
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
                if (!Renderer.BeginPrediction()) { predictionReady = false; return true; }
            }
            long lead = recoveringGaps ? 0
                : Math.Clamp((long)(predictionDrawMs * 10_000) + 2_500, 2_500, Math.Min(15_000, interval / 4));
            if (now < due - lead) { deadline!.WaitUntil(due - lead); return true; }
            predictionReady ??= Renderer.PredictionReady();
            long retryAt = recoveringGaps ? Math.Min(expires, due + 20_000) : due;
            if (predictionReady == null && Clock.Now < retryAt) { deadline!.WaitUntil(retryAt); return true; }
            predictionDue += interval;
            if (predictionReady != true || !WaitForFrameSlot(expires) || Clock.Now > expires || !CanShowPrediction(requested)) return true;

            PrepareDraw();
            if (Clock.Now > expires || !CanShowPrediction(requested)) return true;
            var frame = Renderer.RenderPrediction(due - predictionArrival, interval, host.Sharpness, host.Upscaler);
            predictionDrawMs = Renderer.LastUploadDrawMs;
            while (Clock.Now < due && CanShowPrediction(requested)) deadline!.WaitUntil(due);
            if (Clock.Now <= expires && CanShowPrediction(requested))
                predictionShown |= Present(frame);
            return true;
        }
    }
}
