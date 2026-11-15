using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XenoCapture.Engine;

internal sealed class RecordingResize : IDisposable
{
    readonly RecordingOptions options;
    D3DPreview.RecordingScaler? gpu;
    VideoScaler? cpu;

    internal RecordingResize(RecordingOptions options, bool useCpu)
    {
        this.options = options;
        // Keep the CPU prefilter when reducing the image to avoid aliasing.
        if (useCpu || (long)options.Width * options.Height < 1920 * 1080
            || options.Width < options.SourceWidth || options.Height < options.SourceHeight) return;
        try { gpu = new(options.Width, options.Height); }
        catch (Exception error) when (CanUseCpu(error))
        { Trace.TraceWarning("Recording GPU resize initialization failed (0x{0:X8}).", error.HResult); }
    }

    internal ReadOnlySpan<byte> Resize(Packet packet)
    {
        if (gpu != null)
        {
            try { return gpu.Resize(packet); }
            catch (Exception error) when (CanUseCpu(error))
            {
                Trace.TraceWarning("Recording GPU resize failed (0x{0:X8}).", error.HResult);
                gpu.Dispose();
                gpu = null;
            }
        }
        cpu ??= new(options);
        using var converted = packet.Layout == PixelLayout.Nv12 ? PixelConversion.ToBgra(packet) : null;
        return cpu.Resize(converted ?? packet);
    }

    static bool CanUseCpu(Exception error) => error is ExternalException || D3DPreview.CanFallbackFromUpscaling(error);

    public void Dispose()
    {
        gpu?.Dispose();
        cpu?.Dispose();
    }
}

internal sealed unsafe partial class D3DPreview
{
    internal sealed class RecordingScaler : IDisposable
    {
        static readonly Lazy<byte[]> BicubicBytecode = new(() => ShaderBytecode("Recording.Scale"));
        readonly D3DPreview renderer;
        readonly byte[] pixels;
        readonly Com shader = null!, staging = null!;
        GpuSurface? conversion;

        internal RecordingScaler(int width, int height)
        {
            pixels = new byte[checked(width * height * 4)];
            renderer = new(0, width, height);
            try
            {
                shader = renderer.Shader(BicubicBytecode.Value, 15);
                staging = renderer.Texture(width, height, 87, 3, 0, 0x20000);
            }
            catch { Dispose(); throw; }
        }

        internal ReadOnlySpan<byte> Resize(Packet frame)
        {
            var source = renderer.UploadFrame(frame);
            var view = source.Color;
            if (source.Layout == PixelLayout.Nv12)
            {
                if (conversion == null || conversion.Width != source.Width || conversion.Height != source.Height)
                {
                    conversion?.Dispose();
                    conversion = null;
                    conversion = new(renderer, source.Width, source.Height, true);
                }
                renderer.CopyToRgb(source, conversion);
                view = conversion.View;
            }

            var parameters = new ShaderParameters(source.Width, source.Height, 0, 0, 0, 0);
            var output = new PreparedOutput(view, null, parameters, source.Width, source.Height, false, false);
            renderer.DrawPrepared(output, renderer.FittedViewport(source.Width, source.Height), pixelOverride: shader);
            renderer.UnbindRenderTarget();
            renderer.CopyResource(staging, renderer.target!);
            var map = renderer.MapResource(staging, ResourceMap.Read);
            try
            {
                int rowBytes = renderer.width * 4;
                if (map.Pitch == rowBytes)
                    Marshal.Copy(map.Data, pixels, 0, pixels.Length);
                else
                    for (int y = 0; y < renderer.height; y++)
                        Marshal.Copy(map.Data + (int)(y * map.Pitch), pixels, y * rowBytes, rowBytes);
            }
            finally { renderer.UnmapResource(staging); }
            return pixels;
        }

        public void Dispose()
        {
            conversion?.Dispose();
            staging?.Dispose();
            shader?.Dispose();
            renderer.Dispose();
        }
    }
}
