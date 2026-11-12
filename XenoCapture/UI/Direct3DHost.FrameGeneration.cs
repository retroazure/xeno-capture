using System;
using System.Threading;
using XenoCapture.Engine;

namespace XenoCapture.UI;

internal sealed partial class Direct3DHost
{
    sealed partial class RenderLoop
    {
        HighRateCadence highRateCadence;
        int limitedMultiplier;
        (double Refresh, long Period, PreviewUpscaler Upscaler, AiVideoSettings Ai) highRateSettings;
        long cadenceEpoch;
        long cadenceDue;
        long lastInterpolationInputAt;
        long cadenceScheduledPeriod;
        long cadenceLead = 15_000;
        long lastGeneratedPair;
        int cadencePhase;
        int interpolationStep;

        bool SelectInterpolation(in CaptureSnapshot capture)
        {
            if (multiplier <= 1 || (!capture.HasFrame && !Renderer.HasInterpolationFrame)) return false;

            long candidatePeriod = GetContentPeriod(capture.Duration);
            bool generating = timingFailure == null && Renderer.FrameGenerationFailure == null
                && FrameGenerationPolicy.CanGenerate(candidatePeriod, refreshHz, capture.Width, capture.Height, multiplier);
            if (interpolationStep == 0)
            {
                var settings = (refreshHz, candidatePeriod, host.Upscaler, host.AiVideo);
                if (settings != highRateSettings || !generating || multiplier != 4 || !HighRateCadence.Eligible(candidatePeriod))
                {
                    highRateCadence = default;
                    if (limitedMultiplier != 0) cadenceScheduledPeriod = 0;
                    limitedMultiplier = 0;
                    highRateSettings = settings;
                }
                else if (limitedMultiplier == 0 && highRateCadence.NeedsLowerMultiplier)
                {
                    limitedMultiplier = FrameGenerationPolicy.HighRateFallbackMultiplier(
                        candidatePeriod, refreshHz, host.isFullscreen && host.SmoothSourcePacing);
                    cadenceScheduledPeriod = 0;
                }
            }
            if (generating && limitedMultiplier != 0) multiplier = limitedMultiplier;
            if (!generating) SetFrameGenerationStatus(InterpolationStatus(capture));
            return generating;
        }

        string InterpolationStatus(in CaptureSnapshot capture)
        {
            if (timingFailure != null) return timingFailure;
            if (Renderer.FrameGenerationFailure is { } failure) return failure;
            if ((long)capture.Width * capture.Height > 1920L * 1080) return "1080p input limit";
            if (capture.Duration <= 0) return "Waiting for source timing";
            if (refreshHz < 2) return "Display refresh rate unavailable";
            return $"Needs {10_000_000.0 * multiplier / GetContentPeriod(capture.Duration):0} Hz · screen {refreshHz:0} Hz";
        }

        void RenderInterpolation(FrameGenerationMode requested, bool pending)
        {
            try { deadline ??= new FrameDeadlineWaiter(host.signal); }
            catch (System.ComponentModel.Win32Exception)
            {
                timingFailure = "Accurate frame timing unavailable";
                return;
            }
            if (!Renderer.HasInterpolationFrame)
            {
                PresentFirstInterpolationFrame(requested, pending);
                return;
            }

            // The two-image history must not be overwritten until every
            // intermediate and the current real frame have been displayed.
            if (pending && interpolationStep == 0)
            {
                using var next = TakeLatestFrame();
                if (next != null)
                {
                    if (!PrepareInterpolation(next)) return;
                    lastInterpolationInputAt = Clock.Now;
                    if (!Renderer.FrameWasDuplicate)
                    {
                        interpolationStep = Renderer.HasInterpolationPair ? 1 : multiplier;
                        AlignPair();
                    }
                }
            }
            if (!pending && Clock.Now - lastInterpolationInputAt > Math.Max(contentFramePeriod * 3, 1_000_000))
            {
                Renderer.ResetInterpolation();
                cadenceDue = 0;
                interpolationStep = 0;
                Volatile.Write(ref host.frameGenerationActive, false);
                highRateCadence = default;
                limitedMultiplier = 0;
                SetFrameGenerationStatus("Waiting for video");
                host.signal.WaitOne();
                return;
            }
            if (cadenceScheduledPeriod != contentFramePeriod) StartCadence(Clock.Now);
            // Submit the GPU work just ahead of the presentation
            // deadline, so its variable cost does not shift the phase.
            long interval = contentFramePeriod / multiplier;
            long lead = interpolationStep == 0 ? 0 : Math.Clamp((long)(Renderer.LastUploadDrawMs * 10_000) + 2_500,
                2_500, Math.Min(15_000, interval / 4));
            long renderDue = cadenceDue - lead;
            if (Clock.Now < renderDue)
            {
                deadline!.WaitUntil(renderDue);
                return;
            }
            if (!WaitForFrameSlot() || !IsActive() || !IsCurrentMode(requested)) return;
            if (interpolationStep == 0)
            {
                lock (host.gate) { if (host.latest != null) return; }
            }
            long now = Clock.Now;
            int missed = FrameGenerationPolicy.MissedPhases(now, cadenceDue, contentFramePeriod, multiplier);
            if (missed >= multiplier)
            {
                // A full content interval was lost. Show the newest
                // capture and start a fresh phase sequence.
                using var fresh = TakeLatestFrame();
                if (fresh != null)
                {
                    if (!PrepareInterpolation(fresh)) return;
                    lastInterpolationInputAt = consumedArrival;
                }
                StartCadence(now - contentFramePeriod / multiplier);
                interpolationStep = multiplier;
            }
            else if (missed > 0)
            {
                if (interpolationStep > 0) interpolationStep = Math.Min(multiplier, interpolationStep + missed);
                AdvanceCadence(missed);
                if (now < cadenceDue) return;
            }
            PrepareDraw();
            bool intermediate = interpolationStep > 0 && interpolationStep < multiplier;
            long pair = Renderer.InterpolationPairRevision;
            PresentedFrame shown;
            if (intermediate)
                shown = Renderer.RenderIntermediate(interpolationStep, multiplier, host.Sharpness, host.Upscaler);
            else
            {
                Renderer.RenderCachedFrame(host.Sharpness, host.Upscaler);
                shown = interpolationStep == multiplier ? Renderer.CachedFrame : Renderer.CachedFrame with { Kind = PresentationKind.Repeated };
            }
            while (Clock.Now < cadenceDue && IsActive() && IsCurrentMode(requested))
                deadline!.WaitUntil(cadenceDue);
            if (!IsActive() || !IsCurrentMode(requested)) return;
            if (intermediate && FrameGenerationPolicy.MissedPhases(Clock.Now, cadenceDue, contentFramePeriod, multiplier) > 0) return;
            if (interpolationStep == 0)
            {
                lock (host.gate) { if (host.latest != null) return; }
            }
            // Do not jump back to the older source after a generated phase has already been shown.
            if (FrameGenerationPolicy.ShouldPresentPhase(shown.Kind, intermediate, pair, lastGeneratedPair))
            {
                if (Present(shown) && shown.Kind == PresentationKind.Interpolated)
                    lastGeneratedPair = pair;
            }
            interpolationStep = intermediate ? interpolationStep + 1 : 0;
            AdvanceCadence(1);
        }

        void PresentFirstInterpolationFrame(FrameGenerationMode requested, bool pending)
        {
            if (!pending)
            {
                host.signal.WaitOne();
                return;
            }
            if (!WaitForFrameSlot() || !IsActive() || !IsCurrentMode(requested)) return;
            using var first = TakeLatestFrame();
            if (first == null || !PrepareInterpolation(first)) return;

            PrepareDraw();
            long featureCreations = Renderer.VsrFeatureCreations;
            Renderer.RenderCachedFrame(host.Sharpness, host.Upscaler);
            using var fresh = RefreshAfterAiStartup(featureCreations, contentFramePeriod);
            if (fresh != null && PrepareInterpolation(fresh)) Renderer.RenderCachedFrame(host.Sharpness, host.Upscaler);
            Present(Renderer.CachedFrame);
            cadenceLead = FrameGenerationPolicy.CadenceLead(contentFramePeriod, multiplier, Renderer.FrameAnalysisMs);
            StartCadence(Math.Max(Clock.Now, consumedArrival + cadenceLead));
            interpolationStep = 0;
            lastInterpolationInputAt = Clock.Now;
        }

        bool PrepareInterpolation(Packet packet)
        {
            lastCaptureWidth = packet.Width;
            lastCaptureHeight = packet.Height;
            lastCapturePeriod = packet.Duration;
            contentFramePeriod = GetContentPeriod(packet.Duration);
            if (!Renderer.PrepareInterpolation(packet, contentFramePeriod, true))
            {
                Volatile.Write(ref host.frameGenerationActive, false);
                SetFrameGenerationStatus(Renderer.FrameGenerationFailure ?? "Unavailable");
                Requeue(packet);
                return false;
            }
            if (Renderer.FrameWasDuplicate) Interlocked.Increment(ref host.skippedCaptureDuplicates);

            string content = force30Fps ? "30 FPS game · " : "";
            string limit = limitedMultiplier != 0 ? "4× limited by presentation · " : "";
            SetFrameGenerationStatus($"{content}{multiplier}× · {limit}{10_000_000.0 / contentFramePeriod:0}→{10_000_000.0 * multiplier / contentFramePeriod:0} fps · {Renderer.FrameGenerationDetail}");
            Volatile.Write(ref host.frameGenerationActive, true);
            return true;
        }

        void StartCadence(long epoch)
        {
            cadenceEpoch = epoch;
            cadencePhase = 1;
            cadenceScheduledPeriod = contentFramePeriod;
            cadenceDue = FrameGenerationPolicy.PhaseTime(cadenceEpoch, contentFramePeriod, cadencePhase, multiplier);
        }
        void AdvanceCadence(int phases)
        {
            int position = cadencePhase - 1 + phases;
            cadenceEpoch += (position / multiplier) * contentFramePeriod;
            cadencePhase = position % multiplier + 1;
            cadenceDue = FrameGenerationPolicy.PhaseTime(cadenceEpoch, contentFramePeriod, cadencePhase, multiplier);
        }
        void AlignPair()
        {
            long measuredLead = FrameGenerationPolicy.CadenceLead(contentFramePeriod, multiplier, Renderer.FrameAnalysisMs);
            cadenceLead = measuredLead >= cadenceLead ? measuredLead : cadenceLead + (measuredLead - cadenceLead) / 4;
            long interval = contentFramePeriod / multiplier;
            long desired = consumedArrival + cadenceLead;
            if (cadenceScheduledPeriod != contentFramePeriod || Math.Abs(desired - cadenceDue) > interval / 2)
            {
                // Rejoin a changed cadence or a missed input boundary
                // while giving the last displayed frame a usable interval.
                long firstDue = Math.Max(desired, lastPresentedAt + interval * 3 / 4);
                StartCadence(firstDue - interval);
                return;
            }
            long correction = FrameGenerationPolicy.GameCadenceCorrection(desired, cadenceDue, contentFramePeriod);
            cadenceEpoch += correction;
            cadenceDue += correction;
        }
    }
}
