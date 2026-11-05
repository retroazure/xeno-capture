using System;
using System.Diagnostics;
using System.IO;

using System.Runtime.InteropServices;

namespace XenoCapture.Engine;
internal sealed unsafe partial class D3DPreview
{
    [StructLayout(LayoutKind.Sequential)] readonly record struct FlowParameters(float SourceWidth, float SourceHeight, float AnalysisWidth, float AnalysisHeight, float FlowWidth, float FlowHeight, float Alpha = .5f, uint Flags = 0);
    [StructLayout(LayoutKind.Sequential)] struct BufferUav { public uint Format, Dimension, FirstElement, Elements, Flags; }
    sealed class InterpolationUnavailableException(string message) : Exception(message);
    static byte[] ShaderBytecode(string resource)
    {
        using var input = typeof(D3DPreview).Assembly.GetManifestResourceStream("XenoCapture." + resource)
            ?? throw new InterpolationUnavailableException($"The Direct3D shader {resource} is missing.");
        using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray();
    }
    static readonly Lazy<byte[]> DownsampleCode = new(() => ShaderBytecode("Frame.Downsample"));
    static readonly Lazy<byte[]> CompareCode = new(() => ShaderBytecode("Frame.Compare"));
    static readonly Lazy<byte[]> MotionCode = new(() => ShaderBytecode("Frame.Motion"));
    static readonly Lazy<byte[]> SynthesizeCode = new(() => ShaderBytecode("Frame.Synthesize"));
    static readonly Lazy<byte[]> SlowSynthesizeCode = new(() => ShaderBytecode("Frame.SynthesizeSlow"));
    Com? frameDownsample, frameCompare, frameMotion, frameSynthesize, frameSlowSynthesize, frameConstants, frameStats, frameStatsView, frameStatsReadback;
    readonly GpuSurface?[] frameRgb = new GpuSurface?[2], frameLow = new GpuSurface?[2];
    GpuSurface? frameForward, frameBackward, frameGenerated;
    readonly long[] frameRgbRevision=new long[2];
    int frameCurrent = -1, framePrevious = -1, frameWidth, frameHeight, analysisWidth, analysisHeight, flowWidth, flowHeight;
    int frameAnalysisSamples, frameBudgetOverruns;
    long frameCurrentTime, framePreviousTime, frameCurrentDuration, framePreviousDuration, frameCooldown;
    bool frameDisabled;
    Packet? frameLastDistinct;
    internal long ExactInterpolationRepeats { get; private set; }
    public bool HasInterpolationFrame => frameCurrent >= 0;
    public bool HasInterpolationPair => framePrevious >= 0;
    internal long InterpolationPairRevision => frameCurrent < 0 ? 0 : frameRgbRevision[frameCurrent];
    public bool FrameWasDuplicate { get; private set; }
    public bool CanGenerateMidpoint { get; private set; }
    public string? FrameGenerationFailure { get; private set; }
    public string FrameGenerationDetail { get; private set; } = "Warming up";
    public double FrameAnalysisMs { get; private set; }
    public double FrameMotionConfidence { get; private set; }
    public double GeneratedPixelFraction { get; private set; }
    internal double UnmatchedHighContrastFraction { get; private set; }
    internal bool DisableFrameBudgetForDiagnostics { get; set; }
    internal bool DisableFastBoundaryRecoveryForDiagnostics { get; set; }
    internal bool DisableAlternateWarpForDiagnostics { get; set; }
    internal bool DisableBackgroundRecoveryForDiagnostics { get; set; }
    internal bool ForceFrameGenerationUnavailable { get; set; }
    public PresentedFrame CachedFrame => new(frameCurrentTime, frameCurrentDuration);
    public PresentedFrame PreviousFrame => new(framePreviousTime, framePreviousDuration);
    internal long FrameGenerationSurfaceBytes => HasInterpolationFrame ? (long)frameWidth * frameHeight * 12 + (long)analysisWidth * analysisHeight * 8 + (long)flowWidth * flowHeight * (HardwareOpticalFlowActive ? 42 : 32) : 0;
    void EnsureInterpolation(int w, int h)
    {
        if (ForceFrameGenerationUnavailable) throw new InterpolationUnavailableException("Frame generation is unavailable on this device.");
        if ((long)w * h > 1920L * 1080) throw new InterpolationUnavailableException("Lightweight frame generation supports up to 1080p input.");
        if (frameDownsample != null && frameWidth == w && frameHeight == h) return;
        DisposeInterpolation(); frameWidth = w; frameHeight = h;
        double scale = Math.Min(1, Math.Min(320.0 / w, 180.0 / h));
        analysisWidth = Math.Max(1, (int)Math.Round(w * scale)); analysisHeight = Math.Max(1, (int)Math.Round(h * scale));
        flowWidth = (analysisWidth + 7) / 8; flowHeight = (analysisHeight + 7) / 8;
        frameDownsample = Shader(DownsampleCode.Value, 18); frameCompare = Shader(CompareCode.Value, 18);
        frameMotion = Shader(MotionCode.Value, 18); frameSynthesize = Shader(SynthesizeCode.Value, 18);
        for (int index = 0; index < 2; index++) { frameRgb[index] = new GpuSurface(this, w, h, true, 87); frameLow[index] = new GpuSurface(this, analysisWidth, analysisHeight, false); }
        EnsureOpticalFlow(w,h);
        if (HardwareOpticalFlowActive) frameSlowSynthesize = Shader(SlowSynthesizeCode.Value, 18);
        frameForward = new GpuSurface(this, flowWidth, flowHeight, false, 2); frameBackward = new GpuSurface(this, flowWidth, flowHeight, false, 2);
        frameGenerated = new GpuSurface(this, w, h, false);
        nint p;
        var constantsDesc = new BufferDesc { Size = 32, Usage = 2, Bind = 4, Cpu = 0x10000 };
        Native.Hr(((delegate* unmanaged[Stdcall]<nint, BufferDesc*, nint, nint*, int>)device.Slot(3))(device.P, &constantsDesc, 0, &p)); frameConstants = new(p);
        var statsDesc = new BufferDesc { Size = 32, Usage = 0, Bind = 0x80, Misc = 0x40, Structure = 4 };
        Native.Hr(((delegate* unmanaged[Stdcall]<nint, BufferDesc*, nint, nint*, int>)device.Slot(3))(device.P, &statsDesc, 0, &p)); frameStats = new(p);
        var uav = new BufferUav { Dimension = 1, Elements = 8 };
        Native.Hr(((delegate* unmanaged[Stdcall]<nint, nint, BufferUav*, nint*, int>)device.Slot(8))(device.P, frameStats.P, &uav, &p)); frameStatsView = new(p);
        var readDesc = new BufferDesc { Size = 32, Usage = 3, Cpu = 0x20000 };
        Native.Hr(((delegate* unmanaged[Stdcall]<nint, BufferDesc*, nint, nint*, int>)device.Slot(3))(device.P, &readDesc, 0, &p)); frameStatsReadback = new(p);
    }
    void FrameCompute(Com shader, Com? a, Com? b, Com? forward, Com? backward, Com? output0, Com? output1, Com? stats, int groupsX, int groupsY, float alpha = .5f, uint flags = 0)
    {
        ClearPixelInputs(); ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)context.Slot(33))(context.P, 0, null, 0);
        var parameters = new FlowParameters(frameWidth, frameHeight, analysisWidth, analysisHeight, flowWidth, flowHeight, alpha, flags);
        Mapped mapped; Native.Hr(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)context.Slot(14))(context.P, frameConstants!.P, 0, 4, 0, &mapped));
        try { *(FlowParameters*)mapped.Data = parameters; }
        finally { ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)context.Slot(15))(context.P, frameConstants.P, 0); }
        nint cb = frameConstants.P, linear = sampler!.P;
        nint* sources = stackalloc nint[4]; sources[0] = a?.P ?? 0; sources[1] = b?.P ?? 0; sources[2] = forward?.P ?? 0; sources[3] = backward?.P ?? 0;
        nint* targets = stackalloc nint[3]; targets[0] = output0?.P ?? 0; targets[1] = output1?.P ?? 0; targets[2] = stats?.P ?? 0;
        try
        {
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)context.Slot(71))(context.P, 0, 1, &cb);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)context.Slot(70))(context.P, 0, 1, &linear);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)context.Slot(67))(context.P, 0, 4, sources);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, uint*, void>)context.Slot(68))(context.P, 0, 3, targets, null);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, uint, void>)context.Slot(69))(context.P, shader.P, 0, 0);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, void>)context.Slot(41))(context.P, (uint)groupsX, (uint)groupsY, 1);
        }
        finally { ClearComputeBindings(); }
    }
    readonly record struct FrameStatistics(uint Changed,uint Large,uint Sum,uint Samples,uint Active,uint Trusted,uint Generated)
    { public double Mean => Sum / (Math.Max(1.0,Samples)*4095); }
    void ClearFrameStatistics()
    {
        uint* zeros=stackalloc uint[4]; zeros[0]=zeros[1]=zeros[2]=zeros[3]=0;
        ((delegate* unmanaged[Stdcall]<nint,nint,uint*,void>)context.Slot(51))(context.P,frameStatsView!.P,zeros);
    }
    FrameStatistics ReadFrameStatistics()
    {
        ((delegate* unmanaged[Stdcall]<nint,nint,nint,void>)context.Slot(47))(context.P,frameStatsReadback!.P,frameStats!.P);
        Mapped read; Native.Hr(((delegate* unmanaged[Stdcall]<nint,nint,uint,uint,uint,Mapped*,int>)context.Slot(14))(context.P,frameStatsReadback.P,0,1,0,&read));
        try { var data=(uint*)read.Data; return new(data[0],data[1],data[2],data[3],data[4],data[5],data[6]); }
        finally { ((delegate* unmanaged[Stdcall]<nint,nint,uint,void>)context.Slot(15))(context.P,frameStatsReadback.P,0); }
    }
    bool SkipRepeatedCapture(long start)
    {
        FrameWasDuplicate = true;
        framePrevious = -1;
        CanGenerateMidpoint = false;
        FrameGenerationDetail = "Repeated capture frame skipped";
        FrameAnalysisMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return true;
    }
    public bool PrepareInterpolation(Packet frame, long contentDuration = 0, bool cappedGame = false)
    {
        FrameWasDuplicate=false;
        if (frameDisabled) return false;
        long start=Stopwatch.GetTimestamp(), duration=contentDuration>0 ? contentDuration : frame.Duration;
        try
        {
            EnsureInterpolation(frame.Width,frame.Height);
            int previous=frameCurrent, candidate=frameCurrent==0 ? 1 : 0;
            if (cappedGame && previous >= 0 && frame.IsExactVideoRepeatOf(frameLastDistinct))
            {
                ExactInterpolationRepeats++;
                return SkipRepeatedCapture(start);
            }
            CopyToRgb(UploadFrame(frame),frameRgb[candidate]!);frameRgbRevision[candidate]=++sourceContentRevision;
            FrameCompute(frameDownsample!,frameRgb[candidate]!.View,null,null,null,frameLow[candidate]!.Attachment,null,null,(analysisWidth+15)/16,(analysisHeight+7)/8);
            bool timely = previous >= 0 && frame.Time > frameCurrentTime
                && frame.Time - frameCurrentTime <= Math.Max(duration, 1) * 5 / 2;
            bool withinBudget = Clock.Now >= frameCooldown;
            bool compared = cappedGame && previous >= 0;
            bool batched = compared && timely && withinBudget && HardwareOpticalFlowActive;
            FrameStatistics stats = default;
            if (compared || (timely && withinBudget))
            {
                ClearFrameStatistics();
                FrameCompute(frameCompare!, frameLow[previous]!.View, frameLow[candidate]!.View,
                    null, null, null, null, frameStatsView, (analysisWidth+15)/16, (analysisHeight+7)/8);
            }
            if (compared)
            {
                // Exact duplicates already bypassed the GPU. Queue hardware
                // analysis alongside comparison and map their shared counters once.
                int flowResult = batched ? AnalyzeHardwareFlow(previous, candidate) : 0;
                stats = ReadFrameStatistics();
                if ((stats.Changed == 0 && stats.Mean < .00075)
                    || (stats.Changed < stats.Samples * .0001 && stats.Mean < .00015))
                    return SkipRepeatedCapture(start);
                CheckOpticalFlowResult(flowResult);
            }
            var retained = cappedGame ? frame.Retain() : null;
            frameLastDistinct?.Dispose();
            frameLastDistinct = retained;
            framePrevious=previous; frameCurrent=candidate;
            framePreviousTime=frameCurrentTime; framePreviousDuration=frameCurrentDuration;
            frameCurrentTime=frame.Time; frameCurrentDuration=duration; CanGenerateMidpoint=false;
            if (previous < 0) { FrameAnalysisMs = 0; FrameGenerationDetail = "Warming up"; return true; }
            if (!timely) { FrameGenerationDetail = "Timing reset"; return true; }
            if (!withinBudget) { FrameGenerationDetail = "GPU budget fallback"; return true; }
            if (!batched)
            {
                if (HardwareOpticalFlowActive) CheckOpticalFlowResult(AnalyzeHardwareFlow(previous, candidate));
                else FrameCompute(frameMotion!, frameLow[previous]!.View, frameLow[candidate]!.View, null, null,
                    frameForward!.Attachment, frameBackward!.Attachment, frameStatsView, flowWidth, flowHeight, .5f, cappedGame ? 1u : 0u);
                stats = ReadFrameStatistics();
            }
            FrameAnalysisMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            FrameMotionConfidence=stats.Active==0 ? 0 : stats.Trusted/(double)stats.Active;
            bool stationary=stats.Changed==0 || (!HardwareOpticalFlowActive && stats.Mean<(cappedGame ? .00005 : .001));
            bool sceneCut=(stats.Mean>.35 && stats.Large>stats.Samples*.8) || (stats.Mean>.12 && FrameMotionConfidence<.15);
            CanGenerateMidpoint=!stationary && !sceneCut && stats.Active>0 && FrameMotionConfidence>=(HardwareOpticalFlowActive ? .2 : cappedGame ? .35 : .75);
            FrameGenerationDetail=stationary ? "Static frame" : sceneCut ? "Scene-cut fallback" : CanGenerateMidpoint ? HardwareOpticalFlowActive ? "NVIDIA optical flow" : "Software motion checked" : "Complex-motion fallback";
            if (!DisableFrameBudgetForDiagnostics && ++frameAnalysisSamples>3 && FrameAnalysisMs>Math.Min(HardwareOpticalFlowActive ? 10 : 6,duration/(HardwareOpticalFlowActive ? 20000.0 : 40000.0))) frameBudgetOverruns++; else frameBudgetOverruns=0;
            if (frameBudgetOverruns>=3) { frameCooldown=Clock.Now+20_000_000; CanGenerateMidpoint=false; FrameGenerationDetail="GPU budget fallback"; frameBudgetOverruns=0; }
            return true;
        }
        catch (Exception error) when (error is InterpolationUnavailableException || CanFallbackFromUpscaling(error))
        { DisposeInterpolation(); frameDisabled=true; FrameGenerationFailure="Frame generation unavailable. "+error.Message; return false; }
    }
    public void RenderCachedFrame(double sharpness, PreviewUpscaler upscaler)
    {
        if (!HasInterpolationFrame) throw new InvalidOperationException("No cached source frame.");
        long start = Stopwatch.GetTimestamp(); RenderSource(new(frameRgb[frameCurrent]!.View, null, frameWidth, frameHeight,ContentRevision:frameRgbRevision[frameCurrent]), sharpness, upscaler); LastUploadDrawMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
    public PresentedFrame RenderMidpoint(double sharpness, PreviewUpscaler upscaler) => RenderIntermediate(1, 2, sharpness, upscaler);
    public PresentedFrame RenderIntermediate(int step, int multiplier, double sharpness, PreviewUpscaler upscaler)
    {
        if (multiplier is < 2 or > 4 || step < 1 || step >= multiplier) throw new ArgumentOutOfRangeException(nameof(step));
        if (framePrevious < 0) { RenderCachedFrame(sharpness, upscaler); return CachedFrame with { Duration = frameCurrentDuration / multiplier, Kind = PresentationKind.Repeated }; }
        Com source = frameRgb[framePrevious]!.View;
        long revision = frameRgbRevision[framePrevious];
        PresentationKind kind = PresentationKind.Repeated;
        long start = Stopwatch.GetTimestamp();
        GeneratedPixelFraction = UnmatchedHighContrastFraction = 0;
        if (CanGenerateMidpoint)
        {
            ClearFrameStatistics();
            // Use the content interval so 30 FPS games inside 60 FPS capture get the same checks.
            const long slowContentInterval = 250_000; // 40 FPS in 100 ns units.
            bool slowMotion = frameSlowSynthesize != null && frameCurrentDuration >= slowContentInterval;
            Com shader = slowMotion ? frameSlowSynthesize! : frameSynthesize!;
            uint flags = HardwareOpticalFlowActive ? 2u : 0u;
            if (DisableFastBoundaryRecoveryForDiagnostics) flags |= 4u;
            if (DisableAlternateWarpForDiagnostics) flags |= 8u;
            if (DisableBackgroundRecoveryForDiagnostics) flags |= 16u;
            FrameCompute(shader, frameRgb[framePrevious]!.View, frameRgb[frameCurrent]!.View, frameForward!.View, frameBackward!.View, frameGenerated!.Attachment, null, frameStatsView, (frameWidth + 15) / 16, (frameHeight + 7) / 8, (float)step / multiplier, flags);
            var generated = ReadFrameStatistics();
            // The synthesis pass counts high-contrast pixels with no verified warp.
            int pixels = frameWidth * frameHeight;
            UnmatchedHighContrastFraction = generated.Changed / (double)pixels;
            bool extensiveUnmatchedMotion = generated.Changed > pixels / 8;
            bool unsafeSynthesis = extensiveUnmatchedMotion &&
                (multiplier <= 3 || generated.Generated < generated.Changed * 2);
            if (unsafeSynthesis)
            {
                CanGenerateMidpoint = false;
                FrameGenerationDetail = "Uncertain motion fallback";
            }
            else
            {
                GeneratedPixelFraction = generated.Generated / (double)pixels;
                if (GeneratedPixelFraction >= .001)
                {
                    source = frameGenerated.View;
                    revision = ++sourceContentRevision;
                    kind = PresentationKind.Interpolated;
                }
            }
        }
        Com? rtxInput = kind == PresentationKind.Interpolated ? frameGenerated!.Texture : null;
        RenderSource(new(source, null, frameWidth, frameHeight, ContentRevision: revision, RtxInput: rtxInput), sharpness, upscaler);
        LastUploadDrawMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        // Rejected pairs keep the original cut timing at every output phase.
        long time = kind == PresentationKind.Interpolated
            ? framePreviousTime + (frameCurrentTime - framePreviousTime) * step / multiplier
            : framePreviousTime;
        return new(time, frameCurrentDuration / multiplier, kind);
    }
    public void ResetInterpolation()
    { DisposeInterpolation(); frameDisabled = false; FrameGenerationFailure = null; FrameGenerationDetail = "Warming up"; frameCooldown = 0; frameAnalysisSamples = frameBudgetOverruns = 0; FrameWasDuplicate = false; }
    void DisposeInterpolation()
    {
        fullscreenMotionOutput = null;
        if (context != null)
        {
            ClearPixelInputs(); ClearComputeBindings(); nint empty = 0;
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)context.Slot(71))(context.P, 0, 1, &empty);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)context.Slot(70))(context.P, 0, 1, &empty);
            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)context.Slot(33))(context.P, 0, null, 0);
        }
        frameLastDistinct?.Dispose(); frameLastDistinct = null;
        DisposeOpticalFlow(); // Unregister NVIDIA resources before releasing the input history.
        for (int index = 0; index < 2; index++) { frameRgb[index]?.Dispose(); frameLow[index]?.Dispose(); frameRgb[index] = frameLow[index] = null; }
        frameGenerated?.Dispose(); frameForward?.Dispose(); frameBackward?.Dispose(); frameGenerated = frameForward = frameBackward = null;
        frameStatsView?.Dispose(); frameStats?.Dispose(); frameStatsReadback?.Dispose(); frameConstants?.Dispose();
        frameDownsample?.Dispose(); frameCompare?.Dispose(); frameMotion?.Dispose(); frameSynthesize?.Dispose(); frameSlowSynthesize?.Dispose();
        frameStatsView = frameStats = frameStatsReadback = frameConstants = frameDownsample = frameCompare = frameMotion = frameSynthesize = frameSlowSynthesize = null;
        frameCurrent = framePrevious = -1; CanGenerateMidpoint = false;
    }
}
