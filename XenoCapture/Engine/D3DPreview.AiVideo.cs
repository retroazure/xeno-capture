using System;
using System.Runtime.InteropServices;

namespace XenoCapture.Engine;

internal sealed unsafe partial class D3DPreview
{
    readonly record struct AiStatusSize(int Width, int Height, int Quality);

    AiVideoSettings aiVideoSettings = AiVideoSettings.Off;
    AiStatusSize? previousAiStatusSize;
    Packet? aiLastNativeFrame;
    ImageSource aiLastNativeImage;
    long aiLastUploadRevision;

    public AiVideoSettings AiVideo
    {
        get => aiVideoSettings;
        set
        {
            var settings = AiVideoSettings.Validate(value);
            if (settings == aiVideoSettings) return;

            fullscreenMotionOutput = null;
            if (settings.Enabled != aiVideoSettings.Enabled)
            {
                ClearAiInputCache();
                if (!settings.Enabled && previousUpscaler != PreviewUpscaler.RtxVideo) ResetRtxVideo();
                else ClearRtxVideoFailure();
                nisDisabled = false;
                UpscalingFallbackReason = null;
            }
            aiVideoSettings = settings;
            ResetAiStatus();
        }
    }

    public bool AiVideoActive { get; private set; }
    public string AiVideoStatus { get; private set; } = "Off";
    public string? AiVideoFallbackReason { get; private set; }
    internal int AiVideoOutputWidth => AiVideoActive ? rtxOutput?.Width ?? 0 : 0;
    internal int AiVideoOutputHeight => AiVideoActive ? rtxOutput?.Height ?? 0 : 0;
    internal long AiDuplicateUploadsSkipped { get; private set; }

    Com? TryAiVideo(ImageSource source, double viewportWidth, double viewportHeight)
    {
        var settings = AiVideoSettings.Validate(AiVideo);
        var target = settings.Target(source.Width, source.Height, viewportWidth, viewportHeight);
        var output = TryUpscaleRtxVideo(source, target.Width, target.Height, (uint)settings.Quality, cacheOutput: true);
        RtxVideoRequested = false; // The independent AI filter reports its own status.
        if (output == null)
        {
            string reason = RtxVideoFallbackReason ?? "Unsupported configuration.";
            AiVideoFallbackReason = "AI Video SR unavailable. "
                + reason.Replace("RTX Video unavailable; Normal scaling is active. ", "");
            previousAiStatusSize = null;
            AiVideoStatus = "AI Video SR unavailable · existing scaling fallback";
            return null;
        }

        AiVideoActive = true;
        AiVideoFallbackReason = null;
        var key = new AiStatusSize(target.Width, target.Height, settings.Quality);
        if (previousAiStatusSize != key)
        {
            AiVideoStatus = $"AI Video SR · {settings.QualityName} · {target.Width}×{target.Height}";
            previousAiStatusSize = key;
        }
        return output;
    }

    public void SuspendEnhancements()
    {
        // Stop using SDK resources on this owner thread before another preview starts.
        ResetRtxVideo();
        ClearAiInputCache();
        ResetInterpolation();
        ResetAiStatus();
    }

    void ResetAiStatus()
    {
        previousAiStatusSize = null;
        AiVideoActive = false;
        AiVideoFallbackReason = null;
        AiVideoStatus = AiVideo.Enabled ? "AI Video SR · waiting for video" : "Off";
    }

    public void CompleteAiWork()
    {
        if (rtxSession != 0)
            CheckVsr(rtxApi!.VsrSynchronize(rtxSession), "Complete AI startup before refreshing capture");
    }

    ImageSource UploadProcessedFrame(Packet frame)
    {
        bool duplicate = aiLastUploadRevision == imageUploadRevision && frame.IsExactVideoRepeatOf(aiLastNativeFrame);
        if (duplicate)
        {
            AiDuplicateUploadsSkipped++;
            return aiLastNativeImage;
        }

        var source = UploadFrame(frame);
        var retained = frame.Retain();
        aiLastNativeFrame?.Dispose();
        aiLastNativeFrame = retained;
        aiLastUploadRevision = imageUploadRevision;
        aiLastNativeImage = source;
        return source;
    }

    void ClearAiInputCache()
    {
        if (aiLastNativeFrame is not { } retained) return;
        aiLastNativeFrame = null;
        retained.Dispose();
    }

    // Diagnostic readback only. Live preview keeps the full-resolution result on the GPU.
    internal byte[] ReadbackAiVideo()
    {
        if (!AiVideoActive || rtxOutput == null)
            throw new InvalidOperationException("The AI filter has no output.");

        int outputWidth = rtxOutput.Width;
        int outputHeight = rtxOutput.Height;
        using var staging = Texture(outputWidth, outputHeight, 28, 3, 0, 0x20000);
        CopyResource(staging, rtxOutput.Texture);
        var map = MapResource(staging, ResourceMap.Read);
        var pixels = new byte[checked(outputWidth * outputHeight * 4)];
        try
        {
            int rowBytes = outputWidth * 4;
            for (int y = 0; y < outputHeight; y++)
                Marshal.Copy(map.Data + y * (int)map.Pitch, pixels, y * rowBytes, rowBytes);
            for (int i = 0; i < pixels.Length; i += 4)
                (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        }
        finally { UnmapResource(staging); }
        return pixels;
    }
}
