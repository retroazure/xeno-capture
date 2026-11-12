using System;
using System.IO;
using System.Runtime.InteropServices;

namespace XenoCapture.Engine;

internal sealed unsafe partial class D3DPreview
{
    const uint NvidiaVendorId = 0x10DE;
    const uint VsrSuccess = 1;
    const uint VsrInitializationLost = 0xBAD00007;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct VideoAdapterDescription
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSystemId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long Luid;
    }

    sealed class RtxVideoUnavailableException(string message) : Exception(message);

    readonly record struct RtxVideoSize(int InputWidth, int InputHeight, int OutputWidth, int OutputHeight, uint Quality);
    readonly record struct VsrImageKey(nint Source, long Revision, int Width, int Height, uint Quality);

    FileStream? verifiedRtxModel;
    nint rtxSession;
    GpuSdk.Api? rtxApi;
    GpuSurface? rtxRgb;
    GpuSurface? rtxOutput;
    int rtxSourceWidth, rtxSourceHeight, rtxOutputWidth, rtxOutputHeight;
    bool rtxDisabled;
    RtxVideoSize? rtxFailureSize;
    VsrImageKey? lastVsrImage;
    int lastVsrQuality = -1;

    internal long VsrEvaluations { get; private set; }
    internal long VsrDirectInputs { get; private set; }
    internal bool DisableVsrDirectInputForDiagnostics { get; set; }
    internal long VsrInitializationRecoveries { get; private set; }
    internal long VsrFeatureCreations { get; private set; }
    internal long VsrOutputCacheHits { get; private set; }
    public bool RtxVideoRequested { get; private set; }
    public string? RtxVideoFallbackReason { get; private set; }
    internal bool ForceRtxVideoUnavailable { get; set; }
    internal bool ForceRtxVideoBltUnavailable { get; set; }
    internal bool DisableRtxRequestForDiagnostics { get; set; }
    internal string RtxVideoAdapter { get; private set; } = "Not queried";
    internal long RtxVideoSurfaceBytes => rtxRgb == null ? 0
        : (long)rtxSourceWidth * rtxSourceHeight * 4 + (long)rtxOutputWidth * rtxOutputHeight * 4;

    void CheckRtxAdapter()
    {
        if (ForceRtxVideoUnavailable)
            throw new RtxVideoUnavailableException("RTX Video is unavailable for this request.");

        using var dxgi = device.Query(new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"))
            ?? throw new RtxVideoUnavailableException("Cannot query the preview adapter.");
        nint adapterHandle;
        // IDXGIDevice::GetAdapter, then IDXGIAdapter::GetDesc.
        Native.Hr(((delegate* unmanaged[Stdcall]<nint, nint*, int>)dxgi.Slot(7))(dxgi.P, &adapterHandle));
        using var adapter = new Com(adapterHandle);
        VideoAdapterDescription description;
        Native.Hr(((delegate* unmanaged[Stdcall]<nint, VideoAdapterDescription*, int>)adapter.Slot(8))(adapter.P, &description));
        RtxVideoAdapter = new string(description.Description, 0, 128).TrimEnd('\0');
        if (description.VendorId != NvidiaVendorId)
            throw new RtxVideoUnavailableException($"The preview adapter is {RtxVideoAdapter}. RTX Video requires an NVIDIA RTX adapter.");
    }

    void CheckVsr(uint result, string operation)
    {
        if (result == VsrSuccess) return;

        Native.Hr(GetDeviceRemovalReason());
        throw new RtxVideoUnavailableException($"{operation} failed (SDK 0x{result:X8}).");
    }

    void EnsureRtxVideo(int inputWidth, int inputHeight, int outputWidth, int outputHeight)
    {
        if (rtxSession != 0 && inputWidth == rtxSourceWidth && inputHeight == rtxSourceHeight
            && outputWidth == rtxOutputWidth && outputHeight == rtxOutputHeight) return;

        if (rtxSession == 0)
        {
            CheckRtxAdapter();
            rtxApi = GpuSdk.Library;
            verifiedRtxModel = GpuSdk.OpenVerifiedModel(rtxApi.Directory);
            string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Xeno Capture", "ngx-cache");
            Directory.CreateDirectory(cache);
            CheckVsr(rtxApi.VsrCreate(device.P, rtxApi.Directory, cache, out rtxSession), "Create NVIDIA VSR");
            VsrFeatureCreations++;
        }
        else
        {
            CheckVsr(rtxApi!.VsrSynchronize(rtxSession), "Complete NVIDIA VSR before resize");
        }

        ClearPixelInputs();
        ClearComputeBindings();
        rtxOutput?.Dispose();
        rtxOutput = null;
        lastVsrImage = null;
        if (rtxRgb == null || inputWidth != rtxSourceWidth || inputHeight != rtxSourceHeight)
        {
            rtxRgb?.Dispose();
            rtxRgb = null;
            rtxRgb = new GpuSurface(this, inputWidth, inputHeight, true);
        }
        rtxOutput = new GpuSurface(this, outputWidth, outputHeight, false);
        rtxSourceWidth = inputWidth;
        rtxSourceHeight = inputHeight;
        rtxOutputWidth = outputWidth;
        rtxOutputHeight = outputHeight;
    }

    Com PrepareVsrInput(ImageSource source)
    {
        if (!DisableVsrDirectInputForDiagnostics && source.RtxInput is { } input)
        {
            ClearPixelInputs();
            ClearComputeBindings();
            VsrDirectInputs++;
            return input;
        }

        CopyToRgb(source, rtxRgb!);
        return rtxRgb!.Texture;
    }

    uint EvaluateVsr(Com input, in RtxVideoSize size) =>
        rtxApi!.VsrEvaluate(rtxSession, input.P, rtxOutput!.Texture.P, (uint)size.InputWidth, (uint)size.InputHeight,
            (uint)size.OutputWidth, (uint)size.OutputHeight, DisableRtxRequestForDiagnostics ? 0u : size.Quality);

    Com? TryUpscaleRtxVideo(ImageSource source, int outputWidth, int outputHeight, uint quality = 2, bool cacheOutput = false)
    {
        var size = new RtxVideoSize(source.Width, source.Height, outputWidth, outputHeight, quality);
        if (rtxDisabled && rtxFailureSize == size) return null;

        try
        {
            if (DisableRtxRequestForDiagnostics && (outputWidth > source.Width * 4L || outputHeight > source.Height * 4L))
                throw new RtxVideoUnavailableException("Diagnostic bicubic comparison is limited to 4× scaling.");

            EnsureRtxVideo(source.Width, source.Height, outputWidth, outputHeight);
            var key = new VsrImageKey(source.Color.P, source.ContentRevision, outputWidth, outputHeight, quality);
            if (cacheOutput && !DisableRtxRequestForDiagnostics && source.ContentRevision != 0 && lastVsrImage == key)
            {
                VsrOutputCacheHits++;
                RtxVideoRequested = true;
                return rtxOutput!.View;
            }

            if (lastVsrQuality >= 0 && lastVsrQuality != (int)quality)
                CheckVsr(rtxApi!.VsrSynchronize(rtxSession), "Complete NVIDIA VSR before quality change");
            Com input = PrepareVsrInput(source);
            if (ForceRtxVideoBltUnavailable)
                throw new RtxVideoUnavailableException("Forced SDK evaluation failure.");

            uint result = EvaluateVsr(input, size);
            if (result == VsrInitializationLost)
            {
                // A retained preview can outlive the other window's SDK shutdown.
                // Recreate once when the runtime reports its initialization was lost.
                DisposeRtxVideo();
                EnsureRtxVideo(source.Width, source.Height, outputWidth, outputHeight);
                VsrInitializationRecoveries++;
                input = PrepareVsrInput(source);
                result = EvaluateVsr(input, size);
            }

            CheckVsr(result, "Evaluate NVIDIA VSR");
            VsrEvaluations++;
            lastVsrQuality = (int)quality;
            lastVsrImage = DisableRtxRequestForDiagnostics ? null : key;
            RtxVideoRequested = !DisableRtxRequestForDiagnostics;
            RtxVideoFallbackReason = null;
            rtxDisabled = false;
            rtxFailureSize = null;
            return rtxOutput!.View;
        }
        catch (Exception error) when (error is RtxVideoUnavailableException or DllNotFoundException or EntryPointNotFoundException
            or IOException or InvalidDataException or UnauthorizedAccessException or BadImageFormatException || CanFallbackFromUpscaling(error))
        {
            DisposeRtxVideo();
            rtxDisabled = true;
            rtxFailureSize = size;
            RtxVideoFallbackReason = "RTX Video unavailable; Normal scaling is active. " + error.Message;
            return null;
        }
    }

    void ResetRtxVideo()
    {
        DisposeRtxVideo();
        ClearRtxVideoFailure();
    }

    void ClearRtxVideoFailure()
    {
        rtxDisabled = false;
        rtxFailureSize = null;
        RtxVideoFallbackReason = null;
    }

    void DisposeRtxVideo()
    {
        RtxVideoRequested = false;
        lastVsrImage = null;
        lastVsrQuality = -1;
        if (rtxRgb != null || rtxOutput != null)
        {
            ClearPixelInputs();
            ClearComputeBindings();
        }
        if (rtxSession != 0)
        {
            rtxApi!.VsrDestroy(rtxSession);
            rtxSession = 0;
        }
        verifiedRtxModel?.Dispose();
        verifiedRtxModel = null;
        rtxOutput?.Dispose();
        rtxRgb?.Dispose();
        rtxOutput = null;
        rtxRgb = null;
    }
}
