using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

using System.Threading;

namespace XenoCapture.Engine;

// Immediate-context calls stay on one render thread. The swap chain uses flip
// discard, two buffers and a one-frame queue; frames never pass through WPF.
internal sealed unsafe partial class D3DPreview : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct SwapDesc
    {
        public uint Width, Height, Format, Stereo, Samples, Quality, Usage, Count, Scaling, Effect, Alpha, Flags;
    }
    [StructLayout(LayoutKind.Sequential)] struct TextureDesc
    { public uint Width, Height, Mips, ArraySize, Format, Samples, Quality, Usage, Bind, Cpu, Misc; }
    [StructLayout(LayoutKind.Sequential)] struct BufferDesc
    { public uint Size, Usage, Bind, Cpu, Misc, Structure; }
    [StructLayout(LayoutKind.Sequential)] struct Mapped { public nint Data; public uint Pitch, Depth; }
    [StructLayout(LayoutKind.Sequential)] struct SamplerDesc
    { public uint Filter, U, V, W; public float Bias; public uint Anisotropy, Comparison; public float R, G, B, A, MinLod, MaxLod; }
    [StructLayout(LayoutKind.Sequential)] struct Viewport { public float X, Y, Width, Height, MinDepth, MaxDepth; }
    [StructLayout(LayoutKind.Sequential)] struct PresentParameters
    { public uint DirtyRectCount; public nint DirtyRects, ScrollRect, ScrollOffset; }
    [StructLayout(LayoutKind.Sequential)] internal struct DxgiFrameStatistics
    {
        public uint PresentCount, PresentRefreshCount, SyncRefreshCount;
        public long SyncQpcTime, SyncGpuTime;

        internal long DisplayedAt(double refreshHz)
        {
            int refreshDelta = unchecked((int)(PresentRefreshCount - SyncRefreshCount));
            return (long)(SyncQpcTime * (10_000_000.0 / Stopwatch.Frequency)
                + refreshDelta * (10_000_000.0 / refreshHz));
        }
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForMultipleObjects(uint count, nint* handles, int waitAll, uint milliseconds);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
    Com device = null!, context = null!;
    Com? swap, swap2, target, renderView, vertexShader, pixelShader, aiPixelShader, sampler, constants, image, imageView, chroma, chromaView;
    nint frameReady;
    int disposed;
    bool frameSlotReady;
    int width, height, inputWidth, inputHeight;
    PixelLayout layout; PreviewUpscaler previousUpscaler;
    readonly record struct ShaderParameters(float Width, float Height, float Sharpness, float Nv12, float FullRange, float Bt601, float EdgeStepX = 0, float EdgeStepY = 0,
        float AiFootprintX = 0, float AiFootprintY = 0, float PaddingX = 0, float PaddingY = 0);
    ShaderParameters previousParameters;
    bool parametersValid;long sourceContentRevision,imageUploadRevision;
    static readonly Lazy<byte[]> VertexBytecode = new(() => ShaderBytecode("Preview.Vertex"));
    static readonly Lazy<byte[]> PixelBytecode = new(() => ShaderBytecode("Preview.Pixel"));
    static readonly Lazy<byte[]> AiPixelBytecode = new(() => ShaderBytecode("Preview.AiPixel"));
    bool aiPixelShaderBound;
    internal long ConstantUploads { get; private set; }
    internal long TargetClears { get; private set; }
    public double LastUploadDrawMs { get; private set; }
    internal float PresentationScale { get; set; } = 1;
    PreparedOutput? fullscreenMotionOutput;
    internal bool EdgeSmoothing { get; set; }
    internal bool DiagnosticAiResampling { get; set; }
    internal bool DiagnosticDisableProcessedCache { get; set; }
    internal bool RetainFullscreenOutput { get; set; }
    internal bool CanRedrawFullscreenMotion => fullscreenMotionOutput.HasValue;
    public D3DPreview(nint hwnd, int width, int height, bool deferTarget = false)
    {
        this.width = Math.Max(1, width); this.height = Math.Max(1, height);
        try
        {
            Native.CreateD3D11Device(out nint devicePointer, out nint contextPointer);
            device = new(devicePointer);
            context = new(contextPointer);
            if (hwnd != 0) AttachWindow(hwnd, width, height);
            else if (!deferTarget) CreateTarget();
            // Cache embedded bytecode, while each device owns its GPU shaders.
            vertexShader = Shader(VertexBytecode.Value, 12); pixelShader = Shader(PixelBytecode.Value, 15);
            var sd = new SamplerDesc { Filter = 0x15, U = 3, V = 3, W = 3, Comparison = 1, MaxLod = float.MaxValue };
            nint p; Native.Hr(((delegate* unmanaged[Stdcall]<nint, SamplerDesc*, nint*, int>)device.Slot(23))(device.P, &sd, &p)); sampler = new(p);
            var bd = new BufferDesc { Size = (uint)sizeof(ShaderParameters), Usage = 2, Bind = 4, Cpu = 0x10000 };
            Native.Hr(((delegate* unmanaged[Stdcall]<nint, BufferDesc*, nint, nint*, int>)device.Slot(3))(device.P, &bd, 0, &p)); constants = new(p);
            ((delegate* unmanaged[Stdcall]<nint, uint, void>)context.Slot(24))(context.P, 4);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, uint, void>)context.Slot(11))(context.P, vertexShader.P, 0, 0);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, uint, void>)context.Slot(9))(context.P, pixelShader.P, 0, 0);
            p = sampler.P; ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)context.Slot(10))(context.P, 0, 1, &p);
            p = constants.P; ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)context.Slot(16))(context.P, 0, 1, &p);
        }
        catch { Dispose(); throw; }
    }
    public void AttachWindow(nint hwnd, int w, int h)
    {
        if (target != null) throw new InvalidOperationException("The renderer already has a presentation target.");
        PresentationScale = 1;
        fullscreenMotionOutput = null;
        width = Math.Max(1, w); height = Math.Max(1, h);
        using var dxgi = device.Query(new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"))
            ?? throw new InvalidOperationException("The preview adapter is unavailable.");
        nint p;
        Native.Hr(((delegate* unmanaged[Stdcall]<nint, nint*, int>)dxgi.Slot(7))(dxgi.P, &p));
        using var adapter = new Com(p);
        Guid factoryId = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
        Native.Hr(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)adapter.Slot(6))(adapter.P, &factoryId, &p));
        using var factory = new Com(p);
        var desc = new SwapDesc { Width = (uint)width, Height = (uint)height, Format = 87, Samples = 1, Usage = 0x20, Count = 2, Effect = 4, Flags = 0x40 };
        Native.Hr(((delegate* unmanaged[Stdcall]<nint, nint, nint, SwapDesc*, nint, nint, nint*, int>)factory.Slot(15))
            (factory.P, device.P, hwnd, &desc, 0, 0, &p));
        swap = new(p);
        swap2 = swap.Query(new Guid("a8be2ac4-199f-4946-b331-79599fb98de7"))
            ?? throw new InvalidOperationException("DXGI 1.3 is required for low-buffer playback.");
        Native.Hr(((delegate* unmanaged[Stdcall]<nint, uint, int>)swap2.Slot(31))(swap2.P, 1));
        frameReady = ((delegate* unmanaged[Stdcall]<nint, nint>)swap2.Slot(33))(swap2.P);
        CreateTarget();
    }
    internal Com? RetainPresentationSurface() => swap?.Query(new Guid("00000000-0000-0000-c000-000000000046"));

    public void DetachWindow()
    {
        fullscreenMotionOutput = null;
        ClearAiInputCache();
        ResetAiStatus();
        ResetInterpolation();
        // Drop the old flip buffers before another owner attaches a window.
        ReleaseTarget();
        ((delegate* unmanaged[Stdcall]<nint, void>)context.Slot(111))(context.P);
    }
    void ReleaseTarget()
    {
        if (context != null)
            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)context.Slot(33))(context.P, 0, null, 0);
        renderView?.Dispose(); target?.Dispose(); renderView = target = null;
        swap2?.Dispose(); swap?.Dispose(); swap2 = swap = null;
        if (frameReady != 0) { CloseHandle(frameReady); frameReady = 0; }
        frameSlotReady = false;
    }
    void CreateTarget()
    {
        nint p;
        if (swap != null)
        {
            Guid iid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
            Native.Hr(((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)swap.Slot(9))(swap.P, 0, &iid, &p)); target = new(p);
        }
        else target = Texture(width, height, 87, 0, 0x20, 0);
        Native.Hr(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)device.Slot(9))(device.P, target.P, 0, &p)); renderView = new(p);
    }
    public void Resize(int w, int h)
    {
        if (w < 1 || h < 1 || (w == width && h == height)) return;
        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)context.Slot(33))(context.P, 0, null, 0);
        renderView?.Dispose(); target?.Dispose(); renderView = target = null;
        width = w; height = h;
        if (swap != null) Native.Hr(((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, uint, uint, int>)swap.Slot(13))(swap.P, 0, (uint)w, (uint)h, 0, 0x40));
        CreateTarget();
    }
    internal void SubmitPendingDraw() => ((delegate* unmanaged[Stdcall]<nint, void>)context.Slot(111))(context.P);

    public FrameSlotWait WaitForFrameSlot(nint interrupt, uint timeoutMs)
    {
        // Preserve the consumed credit until Present succeeds, including when
        // a configuration change supersedes the frame that was waiting.
        if (frameSlotReady || frameReady == 0) return FrameSlotWait.Ready;
        nint* handles = stackalloc nint[2] { frameReady, interrupt };
        uint result = WaitForMultipleObjects(2, handles, 0, timeoutMs);
        if (result == 0) { frameSlotReady = true; return FrameSlotWait.Ready; }
        if (result == 1) return FrameSlotWait.Interrupted;
        if (result == 258) return FrameSlotWait.TimedOut;
        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "DXGI frame readiness wait failed.");
    }
    Com Texture(int w, int h, uint format, uint usage, uint bind, uint cpu)
    {
        var desc = new TextureDesc { Width = (uint)w, Height = (uint)h, Mips = 1, ArraySize = 1, Format = format, Samples = 1, Usage = usage, Bind = bind, Cpu = cpu };
        nint p; Native.Hr(((delegate* unmanaged[Stdcall]<nint, TextureDesc*, nint, nint*, int>)device.Slot(5))(device.P, &desc, 0, &p)); return new(p);
    }
    Com View(Com texture)
    { nint p; Native.Hr(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)device.Slot(7))(device.P, texture.P, 0, &p)); return new(p); }
    Com Shader(byte[] bytecode, int slot)
    {
        fixed (byte* data = bytecode)
        {
            nint p; Native.Hr(((delegate* unmanaged[Stdcall]<nint, nint, nuint, nint, nint*, int>)device.Slot(slot))(device.P, (nint)data, (nuint)bytecode.Length, 0, &p)); return new(p);
        }
    }
    void SetPreviewPixelShader(bool aiResampling)
    {
        if (aiPixelShaderBound == aiResampling) return;
        var shader = aiResampling ? aiPixelShader ??= Shader(AiPixelBytecode.Value, 15) : pixelShader!;
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, uint, void>)context.Slot(9))(context.P, shader.P, 0, 0);
        aiPixelShaderBound = aiResampling;
    }
    void Upload(Com texture, byte* source, int pitch, int rows)
    {
        Mapped map; Native.Hr(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)context.Slot(14))(context.P, texture.P, 0, 4, 0, &map));
        try
        {
            if (map.Pitch == pitch) Buffer.MemoryCopy(source, (void*)map.Data, (long)pitch * rows, (long)pitch * rows);
            else for (int y = 0; y < rows; y++) Buffer.MemoryCopy(source + y * pitch, (byte*)map.Data + y * map.Pitch, map.Pitch, pitch);
        }
        finally { ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)context.Slot(15))(context.P, texture.P, 0); }
    }
    void SetGraphicsParameters(ShaderParameters parameters)
    {
        if (!parametersValid || parameters != previousParameters)
        {
            Mapped cb; Native.Hr(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)context.Slot(14))(context.P, constants!.P, 0, 4, 0, &cb));
            try { *(ShaderParameters*)cb.Data = parameters; }
            finally { ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)context.Slot(15))(context.P, constants.P, 0); }
            previousParameters = parameters; parametersValid = true; ConstantUploads++;
        }
    }
    readonly record struct ImageSource(Com Color, Com? Chroma, int Width, int Height, PixelLayout Layout = PixelLayout.Bgr32, bool FullRange = false, bool Bt601 = false,long ContentRevision=0, Com? RtxInput = null);
    ImageSource UploadFrame(Packet frame)
    {
        if (frame.Width != inputWidth || frame.Height != inputHeight || frame.Layout != layout || image == null)
        {
            nint* empty = stackalloc nint[2]; empty[0] = empty[1] = 0;
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)context.Slot(8))(context.P, 0, 2, empty);
            imageView?.Dispose(); image?.Dispose(); chromaView?.Dispose(); chroma?.Dispose(); chroma = chromaView = null;
            inputWidth = frame.Width; inputHeight = frame.Height; layout = frame.Layout;
            image = Texture(inputWidth, inputHeight, layout == PixelLayout.Nv12 ? 61u : 87u, 2, 8, 0x10000); imageView = View(image);
            if (layout == PixelLayout.Nv12) { chroma = Texture(inputWidth / 2, inputHeight / 2, 49, 2, 8, 0x10000); chromaView = View(chroma); }
        }
        fixed (byte* data = frame.Bytes)
        {
            Upload(image, data, inputWidth * (layout == PixelLayout.Nv12 ? 1 : 4), inputHeight);
            if (chroma != null) Upload(chroma, data + inputWidth * inputHeight, inputWidth, inputHeight / 2);
        }
        imageUploadRevision++;return new(imageView!, chromaView, inputWidth, inputHeight, layout, frame.FullRange, frame.Bt601,++sourceContentRevision);
    }
    public void Draw(Packet frame, double sharpness, PreviewUpscaler upscaler = PreviewUpscaler.Standard)
    {
        long start = Stopwatch.GetTimestamp();
        bool enhanced = AiVideo.Enabled || ((upscaler is PreviewUpscaler.Enhanced or PreviewUpscaler.RtxVideo) &&
            width > frame.Width && height > frame.Height);
        if (!enhanced || DiagnosticDisableProcessedCache) ClearAiInputCache();
        var source = enhanced && !DiagnosticDisableProcessedCache ? UploadProcessedFrame(frame) : UploadFrame(frame);
        RenderSource(source, sharpness, upscaler);
        LastUploadDrawMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
    readonly record struct PreparedOutput(Com Color, Com? Chroma, ShaderParameters Parameters, int SourceWidth,
        int SourceHeight, bool AiResampling, bool UsePreviewFilters);
    Viewport FittedViewport(int sourceWidth, int sourceHeight, Rectangle? clip = null)
    {
        var area = clip ?? new Rectangle(0, 0, width, height);
        float scale = Math.Min((float)area.Width / sourceWidth, (float)area.Height / sourceHeight);
        float fittedWidth = sourceWidth * scale, fittedHeight = sourceHeight * scale;
        return new Viewport
        {
            X = area.Left + (area.Width - fittedWidth) / 2, Y = area.Top + (area.Height - fittedHeight) / 2,
            Width = fittedWidth, Height = fittedHeight, MaxDepth = 1
        };
    }

    void RenderSource(ImageSource source, double sharpness, PreviewUpscaler upscaler)
    {
        fullscreenMotionOutput = null;
        var viewport = FittedViewport(source.Width, source.Height);
        Com display = source.Color; Com? displayChroma = source.Chroma;
        var ai = AiVideoSettings.Validate(AiVideo);
        bool usePreviewFilters = upscaler != PreviewUpscaler.Performance || ai.Enabled;
        var parameters = new ShaderParameters(source.Width, source.Height, usePreviewFilters ? (float)sharpness : 0,
            source.Layout == PixelLayout.Nv12 ? 1 : 0, source.FullRange ? 1 : 0, source.Bt601 ? 1 : 0);
        UsedEnhancedUpscaling = false; RtxVideoRequested = false;AiVideoActive=false;AiVideoFallbackReason=null;
        if(!ai.Enabled)AiVideoStatus="Off";
        if (upscaler != previousUpscaler)
        {
            if(!ai.Enabled && upscaler != PreviewUpscaler.RtxVideo) ResetRtxVideo(); nisDisabled = false; UpscalingFallbackReason = null; previousUpscaler = upscaler;
        }
        bool enlarge = viewport.Width > source.Width + .5f && viewport.Height > source.Height + .5f;
        bool rtxRendered = false;
        if(ai.Enabled)
        {
            var filtered=TryAiVideo(source,viewport.Width,viewport.Height);
            if(filtered!=null)
            {
                display=filtered;displayChroma=null;parameters=new(AiVideoOutputWidth,AiVideoOutputHeight,(float)sharpness,0,0,0);rtxRendered=true;
                UpscalingFallbackReason=null;if(nisShader!=null)DisposeNis();
            }
            else UpscalingFallbackReason=AiVideoFallbackReason;
        }
        else if (upscaler == PreviewUpscaler.RtxVideo && enlarge)
        {
            int outputWidth = Math.Clamp((int)Math.Round(viewport.Width),source.Width,width), outputHeight = Math.Clamp((int)Math.Round(viewport.Height),source.Height,height);
            var rtxDisplay = TryUpscaleRtxVideo(source,outputWidth,outputHeight,cacheOutput:true);
            if (rtxDisplay != null)
            {
                display = rtxDisplay; displayChroma = null; parameters = new(outputWidth,outputHeight,(float)sharpness,0,0,0); rtxRendered = true;
                UpscalingFallbackReason = null; if (nisShader != null) DisposeNis();
            }
            else if (!nisDisabled) UpscalingFallbackReason = RtxVideoFallbackReason;
        }
        else if (!ai.Enabled && !enlarge && upscaler == PreviewUpscaler.RtxVideo) { ResetRtxVideo(); UpscalingFallbackReason = null; }
        if (!rtxRendered)
        {
            bool basicScaling = upscaler is PreviewUpscaler.Standard or PreviewUpscaler.Performance;
            if (basicScaling || !enlarge)
            {
                if (nisShader != null) DisposeNis();
                if (basicScaling) { nisDisabled = false; if(!ai.Enabled)UpscalingFallbackReason = null; }
            }
            else if (!nisDisabled)
            {
                int outputWidth = Math.Clamp((int)Math.Round(viewport.Width), source.Width, width), outputHeight = Math.Clamp((int)Math.Round(viewport.Height), source.Height, height);
                try
                {
                    display = UpscaleNis(source, outputWidth, outputHeight, sharpness); displayChroma = null;
                    parameters = new(outputWidth, outputHeight, 0, 0, 0, 0); UsedEnhancedUpscaling = true;
                }
                catch (Exception error) when (CanFallbackFromUpscaling(error))
                {
                    DisposeNis(); nisDisabled = true;
                    UpscalingFallbackReason = (upscaler == PreviewUpscaler.RtxVideo ? "RTX Video and Normal upscaling unavailable; " : "Normal upscaling unavailable; ") + "Standard preview is active. " + error.Message;
                }
            }
        }
        var output = new PreparedOutput(display, displayChroma, parameters, source.Width, source.Height,
            rtxRendered || DiagnosticAiResampling, usePreviewFilters);
        DrawPrepared(output, viewport);
        // The renderer owns these views until its next source draw or detach.
        if (RetainFullscreenOutput) fullscreenMotionOutput = output;
    }

    internal void RedrawFullscreenMotion(Rectangle? dock = null)
    {
        if (fullscreenMotionOutput is not { } output) return;
        DrawPrepared(output, FittedViewport(output.SourceWidth, output.SourceHeight, dock), docking: dock.HasValue);
    }

    void DrawPrepared(PreparedOutput output, Viewport viewport, bool docking = false)
    {
        var parameters = output.Parameters;
        if (EdgeSmoothing && output.UsePreviewFilters)
        {
            // Use capture-pixel spacing even after a scaler has enlarged its stair steps.
            parameters = parameters with { EdgeStepX = .8f / output.SourceWidth, EdgeStepY = .8f / output.SourceHeight };
        }
        nint* views = stackalloc nint[2]; views[0] = output.Color.P; views[1] = output.Chroma?.P ?? 0;
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)context.Slot(8))(context.P, 0, 2, views);
        var rt = renderView!.P; ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)context.Slot(33))(context.P, 1, &rt, 0);
        // Animate the final draw rectangle after the upscaler chooses its output size.
        bool transitioning = !docking && PresentationScale < 1;
        if (transitioning)
        {
            viewport.Width *= PresentationScale;
            viewport.Height *= PresentationScale;
            viewport.X = (width - viewport.Width) / 2;
            viewport.Y = (height - viewport.Height) / 2;
        }
        if (output.AiResampling)
        {
            float footprintX = parameters.Width / viewport.Width, footprintY = parameters.Height / viewport.Height;
            if (footprintX > 1.10f || footprintY > 1.10f || footprintX < .85f || footprintY < .85f)
                parameters = parameters with { AiFootprintX = footprintX, AiFootprintY = footprintY };
        }
        bool aiFilters = output.AiResampling && (parameters.EdgeStepX > 0 || parameters.Sharpness > .001f);
        SetPreviewPixelShader(parameters.AiFootprintX > 0 || aiFilters);
        SetGraphicsParameters(parameters);
        // Each flip-discard buffer must be cleared where the video leaves a border.
        if (docking || transitioning || (long)width * output.SourceHeight != (long)height * output.SourceWidth)
        {
            float* black = stackalloc float[4]; black[0] = black[1] = black[2] = 0; black[3] = 1;
            ((delegate* unmanaged[Stdcall]<nint, nint, float*, void>)context.Slot(50))(context.P, rt, black); TargetClears++;
        }

        ((delegate* unmanaged[Stdcall]<nint, uint, Viewport*, void>)context.Slot(44))(context.P, 1, &viewport);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)context.Slot(13))(context.P, 3, 0);
    }
    public bool Present(bool allowBlocking, uint syncInterval)
    {
        if (swap == null) return true;
        const int stillDrawing = unchecked((int)0x887A000A);
        const uint doNotWait = 0x8;

        int status;
        if (allowBlocking)
        {
            status = ((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)swap.Slot(8))(swap.P, syncInterval, 0);
        }
        else
        {
            PresentParameters parameters = default;
            status = ((delegate* unmanaged[Stdcall]<nint, uint, uint, PresentParameters*, int>)swap.Slot(22))
                (swap.P, syncInterval, doNotWait, &parameters);
            if (status == stillDrawing) return false;
        }
        if(status<0){int reason=((delegate* unmanaged[Stdcall]<nint,int>)device.Slot(39))(device.P);throw new COMException($"D3D11 presentation failed (device reason 0x{reason:X8}).",status);}
        if (status != 0) return false; // A status such as occlusion did not enqueue a frame.
        frameSlotReady = false;
        return true;
    }
    internal bool TryGetDisplayFrame(out DxgiFrameStatistics stats)
    {
        DxgiFrameStatistics sample = default;
        bool available = swap != null
            && ((delegate* unmanaged[Stdcall]<nint, DxgiFrameStatistics*, int>)swap.Slot(16))(swap.P, &sample) == 0;
        stats = sample;
        return available && sample.SyncQpcTime > 0;
    }
    internal uint? LastPresentCount()
    {
        uint count;
        return swap != null && ((delegate* unmanaged[Stdcall]<nint, uint*, int>)swap.Slot(17))(swap.P, &count) == 0
            ? count : null;
    }
    public byte[] Readback()
    {
        using var staging = Texture(width, height, 87, 3, 0, 0x20000);
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)context.Slot(47))(context.P, staging.P, target!.P);
        Mapped map; Native.Hr(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)context.Slot(14))(context.P, staging.P, 0, 1, 0, &map));
        var bytes = new byte[width * height * 4];
        try { for (int y = 0; y < height; y++) Marshal.Copy(map.Data + (int)(y * map.Pitch), bytes, y * width * 4, width * 4); }
        finally { ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)context.Slot(15))(context.P, staging.P, 0); }
        return bytes;
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (context != null) ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)context.Slot(33))(context.P, 0, null, 0);
        // Native VSR destruction already waits for queued GPU work.
        DisposeRtxVideo();
        ClearAiInputCache();
        DisposeInterpolation();
        DisposeNis();
        chromaView?.Dispose(); chroma?.Dispose(); imageView?.Dispose(); image?.Dispose(); constants?.Dispose(); sampler?.Dispose(); aiPixelShader?.Dispose(); pixelShader?.Dispose(); vertexShader?.Dispose();
        ReleaseTarget(); context?.Dispose(); device?.Dispose();
    }

}

