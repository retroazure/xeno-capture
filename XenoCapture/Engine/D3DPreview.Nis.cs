using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace XenoCapture.Engine;
internal enum PreviewUpscaler { Standard, Enhanced, RtxVideo, Performance }

internal sealed unsafe partial class D3DPreview
{
    // NIS configuration is a direct SDR port of NVScalerUpdateConfig. Its MIT
    // license and pinned upstream source are in ThirdParty/NIS.
    [StructLayout(LayoutKind.Sequential)] struct NisParameters
    {
        public float DetectRatio, DetectThreshold, MinContrastRatio, RatioNorm;
        public float ContrastBoost, Epsilon, SharpStartY, SharpScaleY;
        public float SharpStrengthMin, SharpStrengthScale, SharpLimitMin, SharpLimitScale;
        public float ScaleX, ScaleY, DstNormX, DstNormY, SrcNormX, SrcNormY;
        public uint InputX, InputY, InputWidth, InputHeight, OutputX, OutputY, OutputWidth, OutputHeight;
        public float Reserved0, Reserved1;
    }
    [StructLayout(LayoutKind.Sequential)] struct InitialTextureData { public nint Data; public uint Pitch, SlicePitch; }
    readonly record struct NisKey(int SourceWidth, int SourceHeight, int Width, int Height, float Sharpness);
    static readonly Lazy<byte[]> NisBytecode = new(() => NisResource("Shader"));
    static readonly Lazy<byte[]> NisScaleData = new(() => NisResource("Scale"));
    static readonly Lazy<byte[]> NisUsmData = new(() => NisResource("Usm"));
    sealed class NisUnavailableException(string message) : Exception(message);
    sealed class GpuSurface : IDisposable
    {
        public readonly int Width, Height;
        public Com Texture = null!, View = null!, Attachment = null!;
        public GpuSurface(D3DPreview owner, int width, int height, bool conversionTarget, uint format = 28)
        {
            Width = width; Height = height;
            try
            {
                Texture = owner.Texture(width, height, format, 0, conversionTarget ? 0x28u : 0x88u, 0);
                View = owner.View(Texture);
                nint p;
                int slot = conversionTarget ? 9 : 8; // RTV / UAV, inferred from the texture format.
                Native.Hr(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)owner.device.Slot(slot))(owner.device.P, Texture.P, 0, &p)); Attachment = new(p);
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { Attachment?.Dispose(); View?.Dispose(); Texture?.Dispose(); }
    }
    Com? nisShader, nisConstants, nisScaleTexture, nisScaleView, nisUsmTexture, nisUsmView;
    GpuSurface? nisConversion;
    readonly List<GpuSurface> nisStages = new();
    NisKey? nisPreviousParameters;
    readonly record struct NisImageKey(long Revision, int Width, int Height, double Sharpness);
    NisImageKey? nisLastImage;
    internal long NisEvaluations { get; private set; }
    internal long NisOutputCacheHits { get; private set; }
    int nisSourceWidth, nisSourceHeight, nisOutputWidth, nisOutputHeight;
    bool nisDisabled;
    public bool UsedEnhancedUpscaling { get; private set; }
    public string? UpscalingFallbackReason { get; private set; }
    internal bool ForceUpscalingUnavailable { get; set; }
    internal int UpscalingPasses => UsedEnhancedUpscaling ? nisStages.Count : 0;
    static byte[] NisResource(string name)
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("XenoCapture.NIS." + name) ?? throw new NisUnavailableException("The enhanced upscaler is missing a resource.");
        using var data = new MemoryStream(); source.CopyTo(data); var bytes = data.ToArray();
        if ((name != "Shader" && bytes.Length != 2048) || (name == "Shader" && (bytes.Length < 4 || BitConverter.ToUInt32(bytes) != 0x43425844))) throw new NisUnavailableException("The enhanced upscaler resource is invalid.");
        return bytes;
    }
    internal static bool CanFallbackFromUpscaling(Exception error) => error is NisUnavailableException || error.HResult is unchecked((int)0x80070057) or unchecked((int)0x8007000E) or unchecked((int)0x887A0004);
    (Com Texture, Com View) NisLut(byte[] bytes)
    {
        fixed (byte* data = bytes)
        {
            var desc = new TextureDesc { Width = 2, Height = 64, Mips = 1, ArraySize = 1, Format = 2, Samples = 1, Usage = 1, Bind = 8 };
            var initial = new InitialTextureData { Data = (nint)data, Pitch = 32, SlicePitch = 2048 };
            nint p; Native.Hr(((delegate* unmanaged[Stdcall]<nint, TextureDesc*, InitialTextureData*, nint*, int>)device.Slot(5))(device.P, &desc, &initial, &p));
            var texture = new Com(p); try { return (texture, View(texture)); } catch { texture.Dispose(); throw; }
        }
    }
    void EnsureNis()
    {
        if (ForceUpscalingUnavailable) throw new NisUnavailableException("Enhanced upscaling is not supported by this device.");
        if (nisShader != null) return;
        if (sizeof(NisParameters) != 112) throw new NisUnavailableException("Invalid enhanced-upscaling constant layout.");
        nisShader = Shader(NisBytecode.Value, 18); // CreateComputeShader, cs_5_0.
        (nisScaleTexture, nisScaleView) = NisLut(NisScaleData.Value);
        (nisUsmTexture, nisUsmView) = NisLut(NisUsmData.Value);
        var desc = new BufferDesc { Size = 112, Usage = 2, Bind = 4, Cpu = 0x10000 };
        nint p; Native.Hr(((delegate* unmanaged[Stdcall]<nint, BufferDesc*, nint, nint*, int>)device.Slot(3))(device.P, &desc, 0, &p)); nisConstants = new(p);
    }
    void SetNisParameters(int sourceWidth, int sourceHeight, int outputWidth, int outputHeight, float sharpness)
    {
        var key = new NisKey(sourceWidth, sourceHeight, outputWidth, outputHeight, sharpness);
        if (nisPreviousParameters == key) return;
        float slider = Math.Clamp(sharpness, 0, 1) - .5f;
        float maxScale = slider >= 0 ? 1.25f : 1.75f, minScale = slider >= 0 ? 1.25f : 1;
        float strengthMin = Math.Max(0, .4f + slider * minScale * 1.2f), strengthMax = 1.6f + slider * maxScale * 1.8f;
        float limitMin = Math.Max(.1f, .14f + slider * minScale * .32f), limitMax = .5f + slider * minScale * .6f;
        var values = new NisParameters
        {
            DetectRatio = 2 * 1127f / 1024, DetectThreshold = 64f / 1024, MinContrastRatio = 2, RatioNorm = 1f / 8,
            ContrastBoost = 1, Epsilon = 1f / 255, SharpStartY = .45f, SharpScaleY = 1f / (.9f - .45f),
            SharpStrengthMin = strengthMin, SharpStrengthScale = strengthMax - strengthMin, SharpLimitMin = limitMin, SharpLimitScale = limitMax - limitMin,
            ScaleX = sourceWidth / (float)outputWidth, ScaleY = sourceHeight / (float)outputHeight,
            DstNormX = 1f / outputWidth, DstNormY = 1f / outputHeight, SrcNormX = 1f / sourceWidth, SrcNormY = 1f / sourceHeight,
            InputWidth = (uint)sourceWidth, InputHeight = (uint)sourceHeight, OutputWidth = (uint)outputWidth, OutputHeight = (uint)outputHeight
        };
        Mapped mapped; Native.Hr(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)context.Slot(14))(context.P, nisConstants!.P, 0, 4, 0, &mapped));
        try { *(NisParameters*)mapped.Data = values; }
        finally { ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)context.Slot(15))(context.P, nisConstants.P, 0); }
        nisPreviousParameters = key;
    }
    void ClearPixelInputs()
    {
        nint* empty = stackalloc nint[2]; empty[0] = empty[1] = 0;
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)context.Slot(8))(context.P, 0, 2, empty);
    }
    void ClearComputeBindings()
    {
        nint* empty = stackalloc nint[4]; empty[0] = empty[1] = empty[2] = empty[3] = 0;
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)context.Slot(67))(context.P, 0, 4, empty);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, uint*, void>)context.Slot(68))(context.P, 0, 3, empty, null);
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, uint, void>)context.Slot(69))(context.P, 0, 0, 0);
    }
    void CopyToRgb(ImageSource source, GpuSurface destination)
    {
        ClearPixelInputs(); ClearComputeBindings();
        SetPreviewPixelShader(aiResampling: false);
        SetGraphicsParameters(new(source.Width, source.Height, 0, source.Layout == PixelLayout.Nv12 ? 1 : 0, source.FullRange ? 1 : 0, source.Bt601 ? 1 : 0));
        nint* input = stackalloc nint[2]; input[0] = source.Color.P; input[1] = source.Chroma?.P ?? 0;
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)context.Slot(8))(context.P, 0, 2, input);
        nint rt = destination.Attachment.P;
        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)context.Slot(33))(context.P, 1, &rt, 0);
        var viewport = new Viewport { Width = destination.Width, Height = destination.Height, MaxDepth = 1 };
        ((delegate* unmanaged[Stdcall]<nint, uint, Viewport*, void>)context.Slot(44))(context.P, 1, &viewport);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)context.Slot(13))(context.P, 3, 0);
        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)context.Slot(33))(context.P, 0, null, 0);
        ClearPixelInputs();
    }
    Com UpscaleNis(ImageSource sourceImage, int outputWidth, int outputHeight, double sharpness)
    {
        EnsureNis();
        var imageKey = new NisImageKey(sourceImage.ContentRevision, outputWidth, outputHeight, sharpness);
        if (sourceImage.ContentRevision != 0 && nisLastImage == imageKey)
        {
            NisOutputCacheHits++;
            return nisStages[^1].View;
        }
        nisLastImage = null;
        ClearPixelInputs();
        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)context.Slot(33))(context.P, 0, null, 0);
        if (nisSourceWidth != sourceImage.Width || nisSourceHeight != sourceImage.Height || nisOutputWidth != outputWidth || nisOutputHeight != outputHeight)
        {
            foreach (var stage in nisStages) stage.Dispose(); nisStages.Clear();
            int w = sourceImage.Width, h = sourceImage.Height;
            while (w < outputWidth || h < outputHeight)
            {
                w = Math.Min(outputWidth, checked(w * 2)); h = Math.Min(outputHeight, checked(h * 2));
                nisStages.Add(new GpuSurface(this, w, h, false));
            }
            nisSourceWidth = sourceImage.Width; nisSourceHeight = sourceImage.Height; nisOutputWidth = outputWidth; nisOutputHeight = outputHeight;
        }
        Com source = sourceImage.Color;
        if (sourceImage.Layout == PixelLayout.Nv12)
        {
            if (nisConversion == null || nisConversion.Width != sourceImage.Width || nisConversion.Height != sourceImage.Height)
            { nisConversion?.Dispose(); nisConversion = new GpuSurface(this, sourceImage.Width, sourceImage.Height, true); }
            CopyToRgb(sourceImage, nisConversion); source = nisConversion.View;
        }
        nint* resources = stackalloc nint[3]; resources[1] = nisScaleView!.P; resources[2] = nisUsmView!.P;
        nint cb = nisConstants!.P, linear = sampler!.P;
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)context.Slot(71))(context.P, 0, 1, &cb);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)context.Slot(70))(context.P, 0, 1, &linear);
        int sourceWidth = sourceImage.Width, sourceHeight = sourceImage.Height;
        try
        {
            for (int index = 0; index < nisStages.Count; index++)
            {
                var stage = nisStages[index];
                SetNisParameters(sourceWidth, sourceHeight, stage.Width, stage.Height, index == nisStages.Count - 1 ? (float)sharpness : 0);
                resources[0] = source.P; nint output = stage.Attachment.P;
                ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)context.Slot(67))(context.P, 0, 3, resources);
                ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, uint*, void>)context.Slot(68))(context.P, 0, 1, &output, null);
                ((delegate* unmanaged[Stdcall]<nint, nint, nint, uint, void>)context.Slot(69))(context.P, nisShader!.P, 0, 0);
                ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, void>)context.Slot(41))(context.P, (uint)((stage.Width + 31) / 32), (uint)((stage.Height + 23) / 24), 1);
                ClearComputeBindings();
                source = stage.View; sourceWidth = stage.Width; sourceHeight = stage.Height;
            }
            NisEvaluations++;
            nisLastImage = imageKey;
            return source;
        }
        finally { ClearComputeBindings(); }
    }
    void DisposeNis()
    {
        if (context != null) { nint empty = 0; ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)context.Slot(71))(context.P, 0, 1, &empty); ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)context.Slot(70))(context.P, 0, 1, &empty); ClearPixelInputs(); ClearComputeBindings(); ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)context.Slot(33))(context.P, 0, null, 0); }
        foreach (var stage in nisStages) stage.Dispose(); nisStages.Clear();
        nisConversion?.Dispose(); nisConversion = null;
        nisConstants?.Dispose(); nisShader?.Dispose(); nisScaleView?.Dispose(); nisScaleTexture?.Dispose(); nisUsmView?.Dispose(); nisUsmTexture?.Dispose();
        nisConstants = nisShader = nisScaleView = nisScaleTexture = nisUsmView = nisUsmTexture = null;
        nisSourceWidth = nisSourceHeight = nisOutputWidth = nisOutputHeight = 0; nisPreviousParameters = null; nisLastImage = null;
    }
}
