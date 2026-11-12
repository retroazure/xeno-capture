using System;
using System.Diagnostics;

namespace XenoCapture.Engine;

internal sealed unsafe partial class D3DPreview
{
    const uint MapDoNotWait = 0x100000;
    static readonly Lazy<byte[]> PredictCode = new(() => ShaderBytecode("Frame.Predict"));
    Com? framePredict;
    bool predictionReadbackPending;
    readonly long[] predictionDownsampleRevision = new long[2];

    internal bool CachePredictionSource(Packet frame, long period, bool allowRepeatSkip)
    {
        FrameWasDuplicate = false;
        if (frameDisabled) return false;
        try
        {
            EnsureInterpolation(frame.Width, frame.Height, predictive: true);
            framePredict ??= Shader(PredictCode.Value, 18);
            long delta = frame.Time - frameCurrentTime;
            if (allowRepeatSkip && frameCurrent >= 0 && delta > 0
                && delta < period - frame.Duration / 4 && frame.IsExactVideoRepeatOf(frameLastDistinct))
            {
                FrameWasDuplicate = true;
                return true;
            }

            int candidate = frameCurrent == 0 ? 1 : 0;
            CopyToRgb(UploadFrame(frame), frameRgb[candidate]!);
            frameRgbRevision[candidate] = ++sourceContentRevision;
            framePrevious = delta >= period / 2 && delta <= period * 3 / 2 ? frameCurrent : -1;
            framePreviousTime = frameCurrentTime;
            frameCurrent = candidate;
            frameCurrentTime = frame.Time;
            frameCurrentDuration = period;
            frameLastDistinct?.Dispose();
            frameLastDistinct = allowRepeatSkip ? frame.Retain() : null;
            return true;
        }
        catch (Exception error) when (error is InterpolationUnavailableException || CanFallbackFromUpscaling(error))
        {
            DisposeInterpolation();
            frameDisabled = true;
            FrameGenerationFailure = "Motion prediction unavailable. " + error.Message;
            return false;
        }
    }

    internal bool BeginPrediction()
    {
        if (!HasInterpolationPair || (predictionReadbackPending && !TryMapFrameStatistics(MapDoNotWait, out _)))
            return false;

        DownsamplePredictionSource(framePrevious);
        DownsamplePredictionSource(frameCurrent);
        ClearFrameStatistics();
        FrameCompute(frameCompare!, frameLow[framePrevious]!.View, frameLow[frameCurrent]!.View, null, null,
            null, null, frameStatsView, (analysisWidth + 15) / 16, (analysisHeight + 7) / 8);
        FrameCompute(frameMotion!, frameLow[framePrevious]!.View, frameLow[frameCurrent]!.View, null, null,
            frameForward!.Attachment, frameBackward!.Attachment, frameStatsView, flowWidth, flowHeight);
        CopyFrameStatistics();
        SubmitPendingDraw();
        predictionReadbackPending = true;
        return true;
    }

    void DownsamplePredictionSource(int index)
    {
        if (predictionDownsampleRevision[index] == frameRgbRevision[index]) return;
        FrameCompute(frameDownsample!, frameRgb[index]!.View, null, null, null,
            frameLow[index]!.Attachment, null, null, (analysisWidth + 15) / 16, (analysisHeight + 7) / 8);
        predictionDownsampleRevision[index] = frameRgbRevision[index];
    }

    // null means the GPU is still working. The caller can drop this output slot.
    internal bool? PredictionReady()
    {
        if (!predictionReadbackPending) return false;
        if (!TryMapFrameStatistics(MapDoNotWait, out var stats)) return null;
        predictionReadbackPending = false;
        return stats.Changed > 0 && stats.Active > 0 && stats.Trusted >= stats.Active * .35
            && stats.Mean <= .35 && stats.Large <= stats.Samples * .8;
    }

    internal PresentedFrame RenderPrediction(long ahead, long interval, double sharpness, PreviewUpscaler upscaler)
    {
        long start = Stopwatch.GetTimestamp();
        float phase = Math.Clamp((float)ahead / (frameCurrentTime - framePreviousTime), 0, .95f);
        FrameCompute(framePredict!, frameRgb[framePrevious]!.View, frameRgb[frameCurrent]!.View,
            frameForward!.View, frameBackward!.View, frameGenerated!.Attachment, null, null,
            (frameWidth + 15) / 16, (frameHeight + 7) / 8, phase);
        RenderSource(new(frameGenerated.View, null, frameWidth, frameHeight,
            ContentRevision: ++sourceContentRevision, RtxInput: frameGenerated.Texture), sharpness, upscaler);
        LastUploadDrawMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return new(frameCurrentTime + ahead, interval, PresentationKind.Predicted);
    }
}
