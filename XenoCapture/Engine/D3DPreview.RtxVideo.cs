using System;
using System.IO;
using System.Runtime.InteropServices;
namespace XenoCapture.Engine;
internal sealed unsafe partial class D3DPreview
{
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct VideoAdapterDescription
    { public fixed char Description[128]; public uint VendorId,DeviceId,SubSystemId,Revision; public nuint DedicatedVideoMemory,DedicatedSystemMemory,SharedSystemMemory; public long Luid; }
    sealed class RtxVideoUnavailableException(string message):Exception(message);
    FileStream? verifiedRtxModel;
    nint rtxSession; GpuSdk.Api? rtxApi; GpuSurface? rtxRgb,rtxOutput;
    int rtxSourceWidth,rtxSourceHeight,rtxOutputWidth,rtxOutputHeight; bool rtxDisabled;
    readonly record struct RtxVideoSize(int InputWidth,int InputHeight,int OutputWidth,int OutputHeight,uint Quality);
    RtxVideoSize? rtxFailureSize;
    readonly record struct VsrImageKey(nint Source,long Revision,int Width,int Height,uint Quality);
    VsrImageKey? lastVsrImage;int lastVsrQuality=-1;
    internal long VsrEvaluations {get;private set;}
    internal long VsrDirectInputs { get; private set; }
    internal bool DisableVsrDirectInputForDiagnostics { get; set; }
    internal long VsrInitializationRecoveries {get;private set;}
    internal long VsrFeatureCreations {get;private set;}
    internal long VsrOutputCacheHits {get;private set;}
    public bool RtxVideoRequested { get; private set; }
    public string? RtxVideoFallbackReason { get; private set; }
    internal bool ForceRtxVideoUnavailable { get; set; }
    internal bool ForceRtxVideoBltUnavailable { get; set; }
    internal bool DisableRtxRequestForDiagnostics { get; set; }
    internal string RtxVideoAdapter { get; private set; }="Not queried";
    internal long RtxVideoSurfaceBytes=>rtxRgb==null ? 0:(long)rtxSourceWidth*rtxSourceHeight*4+(long)rtxOutputWidth*rtxOutputHeight*4;
    void CheckRtxAdapter()
    {
        if(ForceRtxVideoUnavailable) throw new RtxVideoUnavailableException("RTX Video is unavailable for this request.");
        using var dxgi=device.Query(new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c")) ?? throw new RtxVideoUnavailableException("Cannot query the preview adapter.");
        nint p; Native.Hr(((delegate* unmanaged[Stdcall]<nint,nint*,int>)dxgi.Slot(7))(dxgi.P,&p)); using var adapter=new Com(p);
        VideoAdapterDescription description;Native.Hr(((delegate* unmanaged[Stdcall]<nint,VideoAdapterDescription*,int>)adapter.Slot(8))(adapter.P,&description));
        RtxVideoAdapter=new string(description.Description,0,128).TrimEnd('\0');
        if(description.VendorId!=0x10de) throw new RtxVideoUnavailableException("The preview adapter is "+RtxVideoAdapter+". RTX Video requires an NVIDIA RTX adapter.");
    }
    void CheckVsr(uint result,string operation)
    {
        if(result==1) return;
        Native.Hr(((delegate* unmanaged[Stdcall]<nint,int>)device.Slot(39))(device.P));
        throw new RtxVideoUnavailableException(operation+$" failed (SDK 0x{result:X8}).");
    }
    void EnsureRtxVideo(int iw,int ih,int ow,int oh)
    {
        if(rtxSession!=0 && iw==rtxSourceWidth && ih==rtxSourceHeight && ow==rtxOutputWidth && oh==rtxOutputHeight) return;
        if(rtxSession==0)
        {
            CheckRtxAdapter();rtxApi=GpuSdk.Library;verifiedRtxModel=GpuSdk.OpenVerifiedModel(rtxApi.Directory);
            string cache=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Xeno Capture","ngx-cache");System.IO.Directory.CreateDirectory(cache);
            CheckVsr(rtxApi.VsrCreate(device.P,rtxApi.Directory,cache,out rtxSession),"Create NVIDIA VSR");VsrFeatureCreations++;
        }
        else CheckVsr(rtxApi!.VsrSynchronize(rtxSession),"Complete NVIDIA VSR before resize");
        ClearPixelInputs();
        ClearComputeBindings();
        rtxOutput?.Dispose();
        rtxOutput = null;
        lastVsrImage = null;
        if (rtxRgb == null || iw != rtxSourceWidth || ih != rtxSourceHeight)
        {
            rtxRgb?.Dispose();
            rtxRgb = null;
            rtxRgb = new GpuSurface(this, iw, ih, true);
        }
        rtxOutput = new GpuSurface(this, ow, oh, false);
        rtxSourceWidth = iw;
        rtxSourceHeight = ih;
        rtxOutputWidth = ow;
        rtxOutputHeight = oh;
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

    Com? TryUpscaleRtxVideo(ImageSource source,int outputWidth,int outputHeight,uint quality=2,bool cacheOutput=false)
    {
        var size=new RtxVideoSize(source.Width,source.Height,outputWidth,outputHeight,quality);
        if(rtxDisabled && rtxFailureSize==size) return null;
        try
        {
            if(DisableRtxRequestForDiagnostics && (outputWidth>source.Width*4L || outputHeight>source.Height*4L))
                throw new RtxVideoUnavailableException("Diagnostic bicubic comparison is limited to 4× scaling.");
            EnsureRtxVideo(source.Width,source.Height,outputWidth,outputHeight);
            var key=new VsrImageKey(source.Color.P,source.ContentRevision,outputWidth,outputHeight,quality);
            if(cacheOutput&&!DisableRtxRequestForDiagnostics&&source.ContentRevision!=0&&lastVsrImage==key)
            {VsrOutputCacheHits++;RtxVideoRequested=true;return rtxOutput!.View;}
            if(lastVsrQuality>=0&&lastVsrQuality!=(int)quality)CheckVsr(rtxApi!.VsrSynchronize(rtxSession),"Complete NVIDIA VSR before quality change");
            Com input = PrepareVsrInput(source);
            if(ForceRtxVideoBltUnavailable) throw new RtxVideoUnavailableException("Forced SDK evaluation failure.");
            uint result=rtxApi!.VsrEvaluate(rtxSession,input.P,rtxOutput!.Texture.P,(uint)source.Width,(uint)source.Height,(uint)outputWidth,(uint)outputHeight,DisableRtxRequestForDiagnostics ? 0u:quality);
            if(result==0xBAD00007)
            {
                // A retained preview can outlive the other window's SDK shutdown.
                // Recreate once when the runtime reports its initialization was lost.
                DisposeRtxVideo();EnsureRtxVideo(source.Width,source.Height,outputWidth,outputHeight);VsrInitializationRecoveries++;
                input = PrepareVsrInput(source);
                result=rtxApi.VsrEvaluate(rtxSession,input.P,rtxOutput!.Texture.P,(uint)source.Width,(uint)source.Height,(uint)outputWidth,(uint)outputHeight,DisableRtxRequestForDiagnostics ? 0u:quality);
            }
            CheckVsr(result,"Evaluate NVIDIA VSR");
            VsrEvaluations++;lastVsrQuality=(int)quality;lastVsrImage=DisableRtxRequestForDiagnostics ? null : key;
            RtxVideoRequested=!DisableRtxRequestForDiagnostics;RtxVideoFallbackReason=null;rtxDisabled=false;rtxFailureSize=null;return rtxOutput!.View;
        }
        catch(Exception error) when(error is RtxVideoUnavailableException or DllNotFoundException or EntryPointNotFoundException or IOException or InvalidDataException or UnauthorizedAccessException or BadImageFormatException || CanFallbackFromUpscaling(error))
        { DisposeRtxVideo();rtxDisabled=true;rtxFailureSize=size;RtxVideoFallbackReason="RTX Video unavailable; Normal scaling is active. "+error.Message;return null; }
    }
    void ResetRtxVideo(){DisposeRtxVideo();ClearRtxVideoFailure();}
    void ClearRtxVideoFailure(){rtxDisabled=false;rtxFailureSize=null;RtxVideoFallbackReason=null;}
    void DisposeRtxVideo()
    {
        RtxVideoRequested=false;lastVsrImage=null;lastVsrQuality=-1;
        if(rtxRgb!=null || rtxOutput!=null){ClearPixelInputs();ClearComputeBindings();}
        if(rtxSession!=0){rtxApi!.VsrDestroy(rtxSession);rtxSession=0;}
        verifiedRtxModel?.Dispose(); verifiedRtxModel = null;
        rtxOutput?.Dispose();rtxRgb?.Dispose();rtxOutput=rtxRgb=null;
    }
}