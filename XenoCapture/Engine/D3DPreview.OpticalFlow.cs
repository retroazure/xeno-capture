using System;
using System.IO;
using System.Runtime.InteropServices;
namespace XenoCapture.Engine;
internal sealed unsafe partial class D3DPreview
{
    static readonly Lazy<byte[]> HardwareFlowCode = new(() => ShaderBytecode("Frame.Hardware"));
    nint opticalFlowSession; GpuSdk.Api? opticalFlowApi; Com? hardwareFlowShader;
    readonly Com?[] opticalFlowTextures=new Com?[4], opticalFlowViews=new Com?[4];
    internal bool ForceSoftwareFlowForDiagnostics { get; set; }
    internal bool HardwareOpticalFlowActive => opticalFlowSession!=0;
    internal string? OpticalFlowFallbackReason { get; private set; }
    void EnsureOpticalFlow(int width,int height)
    {
        if(ForceSoftwareFlowForDiagnostics) return;
        try
        {
            opticalFlowApi=GpuSdk.Library;
            int result=opticalFlowApi.FlowCreate(device.P,frameRgb[0]!.Texture.P,frameRgb[1]!.Texture.P,(uint)width,(uint)height,out opticalFlowSession,out nint f,out nint b,out nint fc,out nint bc,out uint gw,out uint gh);
            // Returned textures own one COM reference, independently of the session.
            nint[] textures={f,b,fc,bc};
            for(int i=0;i<4;i++) if(textures[i]!=0) opticalFlowTextures[i]=new Com(textures[i]);
            if(result!=0 || opticalFlowSession==0) throw new InterpolationUnavailableException($"NVIDIA optical flow unavailable ({result}).");
            for(int i=0;i<4;i++)
            {
                nint view;Native.Hr(((delegate* unmanaged[Stdcall]<nint,nint,nint,nint*,int>)device.Slot(7))(device.P,opticalFlowTextures[i]!.P,0,&view));opticalFlowViews[i]=new Com(view);
            }
            hardwareFlowShader=Shader(HardwareFlowCode.Value,18);flowWidth=(int)gw;flowHeight=(int)gh;OpticalFlowFallbackReason=null;
        }
        catch(Exception error) when(error is InterpolationUnavailableException or DllNotFoundException or EntryPointNotFoundException or IOException or InvalidDataException or UnauthorizedAccessException or BadImageFormatException || CanFallbackFromUpscaling(error))
        { DisposeOpticalFlow();OpticalFlowFallbackReason=error.Message; }
    }
    int AnalyzeHardwareFlow(int previous, int current)
    {
        ClearPixelInputs(); ClearComputeBindings();
        int result = opticalFlowApi!.FlowEvaluate(opticalFlowSession, (uint)previous, (uint)current);
        if (result == 0)
            FrameCompute(hardwareFlowShader!, opticalFlowViews[0], opticalFlowViews[1], opticalFlowViews[2], opticalFlowViews[3],
                frameForward!.Attachment, frameBackward!.Attachment, frameStatsView, (flowWidth+15)/16, (flowHeight+7)/8);
        return result;
    }
    void CheckOpticalFlowResult(int result)
    {
        if (result == 0) return;
        Native.Hr(((delegate* unmanaged[Stdcall]<nint,int>)device.Slot(39))(device.P));
        throw new InterpolationUnavailableException($"NVIDIA optical flow evaluation failed ({result}).");
    }
    void DisposeOpticalFlow()
    {
        if(opticalFlowSession!=0){opticalFlowApi!.FlowDestroy(opticalFlowSession);opticalFlowSession=0;}
        for(int i=0;i<4;i++){opticalFlowViews[i]?.Dispose();opticalFlowTextures[i]?.Dispose();opticalFlowViews[i]=opticalFlowTextures[i]=null;}
        hardwareFlowShader?.Dispose();hardwareFlowShader=null;
    }
}
