using System;
using System.Runtime.InteropServices;
namespace XenoCapture.Engine;
internal sealed unsafe partial class D3DPreview
{
    AiVideoSettings aiVideoSettings=AiVideoSettings.Off;
    public AiVideoSettings AiVideo
    {
        get=>aiVideoSettings;
        set
        {
            var settings=AiVideoSettings.Validate(value);if(settings==aiVideoSettings)return;
            fullscreenMotionOutput = null;
            if (settings.Enabled != aiVideoSettings.Enabled)
            {
                ClearAiInputCache();
                if (!settings.Enabled && previousUpscaler != PreviewUpscaler.RtxVideo) ResetRtxVideo();
                else ClearRtxVideoFailure();
                nisDisabled = false; UpscalingFallbackReason = null;
            }
            aiVideoSettings = settings;
            ResetAiStatus();
        }
    }
    readonly record struct AiStatusSize(int Width,int Height,int Quality);
    AiStatusSize? previousAiStatusSize;
    public bool AiVideoActive {get;private set;}
    public string AiVideoStatus {get;private set;}="Off";
    public string? AiVideoFallbackReason {get;private set;}
    internal int AiVideoOutputWidth=>AiVideoActive?rtxOutput?.Width??0:0;
    internal int AiVideoOutputHeight=>AiVideoActive?rtxOutput?.Height??0:0;
    Com? TryAiVideo(ImageSource source,double viewportWidth,double viewportHeight)
    {
        var settings=AiVideoSettings.Validate(AiVideo);
        var target=settings.Target(source.Width,source.Height,viewportWidth,viewportHeight);
        var output=TryUpscaleRtxVideo(source,target.Width,target.Height,(uint)settings.Quality,true);
        RtxVideoRequested=false; // Separate status for the independently enabled filter.
        if(output==null)
        {
            AiVideoFallbackReason="AI Video SR unavailable. "+(RtxVideoFallbackReason??"Unsupported configuration.").Replace("RTX Video unavailable; Normal scaling is active. ","");
            previousAiStatusSize=null;AiVideoStatus="AI Video SR unavailable · existing scaling fallback";return null;
        }
        AiVideoActive=true;AiVideoFallbackReason=null;
        var key=new AiStatusSize(target.Width,target.Height,settings.Quality);
        if(previousAiStatusSize!=key)
        {AiVideoStatus=$"AI Video SR · {settings.QualityName} · {target.Width}×{target.Height}";previousAiStatusSize=key;}
        return output;
    }
    public void SuspendEnhancements()
    {
        // Stop using SDK resources on this owner thread before another preview starts.
        ResetRtxVideo();ClearAiInputCache();ResetInterpolation();
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
    {if(rtxSession!=0)CheckVsr(rtxApi!.VsrSynchronize(rtxSession),"Complete AI startup before refreshing capture");}
    Packet? aiLastNativeFrame;ImageSource aiLastNativeImage;long aiLastUploadRevision;
    internal long AiDuplicateUploadsSkipped {get;private set;}
    ImageSource UploadProcessedFrame(Packet frame)
    {
        bool duplicate = aiLastUploadRevision == imageUploadRevision && frame.IsExactVideoRepeatOf(aiLastNativeFrame);
        if(duplicate){AiDuplicateUploadsSkipped++;return aiLastNativeImage;}
        var source=UploadFrame(frame);var retained=frame.Retain();aiLastNativeFrame?.Dispose();aiLastNativeFrame=retained;
        aiLastUploadRevision=imageUploadRevision;return aiLastNativeImage=source;
    }
    void ClearAiInputCache()
    {
        if (aiLastNativeFrame is not { } retained) return;
        aiLastNativeFrame = null;
        retained.Dispose();
    }
    // Diagnostic readback only. The live preview keeps the full-resolution result on the GPU.
    internal byte[] ReadbackAiVideo()
    {
        if(!AiVideoActive||rtxOutput==null)throw new InvalidOperationException("The AI filter has no output.");
        int w=rtxOutput.Width,h=rtxOutput.Height;using var staging=Texture(w,h,28,3,0,0x20000);
        ((delegate* unmanaged[Stdcall]<nint,nint,nint,void>)context.Slot(47))(context.P,staging.P,rtxOutput.Texture.P);
        Mapped map;Native.Hr(((delegate* unmanaged[Stdcall]<nint,nint,uint,uint,uint,Mapped*,int>)context.Slot(14))(context.P,staging.P,0,1,0,&map));
        var pixels=new byte[checked(w*h*4)];
        try
        {
            for(int y=0;y<h;y++)Marshal.Copy(map.Data+y*(int)map.Pitch,pixels,y*w*4,w*4);
            for(int i=0;i<pixels.Length;i+=4)(pixels[i],pixels[i+2])=(pixels[i+2],pixels[i]);
        }
        finally{((delegate* unmanaged[Stdcall]<nint,nint,uint,void>)context.Slot(15))(context.P,staging.P,0);}
        return pixels;
    }
}
