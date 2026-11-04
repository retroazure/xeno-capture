using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XenoCapture.Engine;
internal static class RtxVideoDiagnostics
{
    static void Require(bool condition,string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static async Task Run(string folder)
    {
        Directory.CreateDirectory(folder); var checks = await Task.Run(()=>RunGpu(folder));
        await File.WriteAllTextAsync(Path.Combine(folder,"gpu-results.json"),JsonSerializer.Serialize(checks,new JsonSerializerOptions { WriteIndented=true }));
        foreach (var item in await RunUi(folder)) checks[item.Key] = item.Value;
        checks["result"] = "PASS"; await File.WriteAllTextAsync(Path.Combine(folder,"results.json"),JsonSerializer.Serialize(checks,new JsonSerializerOptions { WriteIndented = true }));
    }
    internal static Dictionary<string,object> RunGpu(string folder)
    {
        Directory.CreateDirectory(folder); using var apartment = new Apartment(); var checks = new Dictionary<string,object>();
        using var frame = FrameGenerationDiagnostics.Pattern(0,1280,720);
        using (var renderer = new D3DPreview(0,2560,1440))
        {
            renderer.Draw(frame,0,PreviewUpscaler.Enhanced); byte[] normal = renderer.Readback();
            renderer.Draw(frame,0,PreviewUpscaler.RtxVideo); byte[] rtx = renderer.Readback();
            bool evaluated = renderer.RtxVideoRequested;
            checks["rtxVideoAdapter"] = renderer.RtxVideoAdapter; checks["rtxSdkEvaluationSucceeded"] = evaluated;
            checks["rtxStatus"] = renderer.RtxVideoFallbackReason ?? "Direct NVIDIA VSR SDK model evaluation succeeded.";
            checks["rtxSurfaceBytes720To1440"] = renderer.RtxVideoSurfaceBytes;
            UpscalingDiagnostics.SavePng(Path.Combine(folder,"normal-1440.png"),normal,2560,1440);
            UpscalingDiagnostics.SavePng(Path.Combine(folder,"vsr-1440.png"),rtx,2560,1440);
            if (!evaluated) Require(renderer.UpscalingFallbackReason != null && normal.AsSpan().SequenceEqual(rtx),"An unavailable SDK did not preserve Normal pixels.");
            if (evaluated)
            {
                // Compare quality levels on the live feature without overlapping NGX sessions on two devices.
                renderer.DisableRtxRequestForDiagnostics = true;
                byte[] withoutRequest;
                try
                {
                    renderer.Draw(frame,0,PreviewUpscaler.RtxVideo);
                    withoutRequest = renderer.Readback();
                }
                finally { renderer.DisableRtxRequestForDiagnostics = false; }
                long difference = 0; for (int i=0;i<rtx.Length;i++) difference += Math.Abs(rtx[i]-withoutRequest[i]);
                checks["vsrVsSdkBicubicAbsoluteDifference"] = difference;
                checks["vsrSha256"] = Convert.ToHexString(SHA256.HashData(rtx));
                checks["sdkBicubicSha256"] = Convert.ToHexString(SHA256.HashData(withoutRequest));
                Require(difference>1000,"Direct VSR output is indistinguishable from SDK bicubic."); checks["aiActivationConfirmed"] = true;
                UpscalingDiagnostics.SavePng(Path.Combine(folder,"sdk-bicubic-1440.png"),withoutRequest,2560,1440);
                foreach (var color in new[] { new byte[]{0,0,0}, new byte[]{255,255,255}, new byte[]{56,101,173}, new byte[]{128,128,128} })
                {
                    using var solid = new Packet(1280*720*4,false,1280,720);
                    for (int i=0;i<solid.Length;i+=4) { solid.Bytes[i]=color[0]; solid.Bytes[i+1]=color[1]; solid.Bytes[i+2]=color[2]; solid.Bytes[i+3]=255; }
                    renderer.Draw(solid,0,PreviewUpscaler.RtxVideo); var pixels = renderer.Readback();
                    int middle = (720*2560+1280)*4;
                    for (int c=0;c<3;c++) Require(Math.Abs(pixels[middle+c]-color[c])<=5,"RTX video conversion altered flat color or range.");
                }
                renderer.Resize(3840,2160); renderer.Draw(frame,.25,PreviewUpscaler.RtxVideo); renderer.Readback();
                Require(renderer.RtxVideoRequested,"Direct VSR processing did not rebuild at 4K."); checks["rtx1440And4kResize"] = "pass";
                using var nv12 = new Packet(1280*720*3/2,false,1280,720,PixelLayout.Nv12);
                Array.Fill(nv12.Bytes,(byte)128,0,nv12.Length);
                foreach (bool fullRange in new[] { false,true }) foreach (bool bt601 in new[] { false,true })
                {
                    nv12.FullRange=fullRange; nv12.Bt601=bt601; renderer.Draw(nv12,0,PreviewUpscaler.Standard); var expected=renderer.Readback();
                    renderer.Draw(nv12,0,PreviewUpscaler.RtxVideo); var pixels=renderer.Readback(); int center=(1080*3840+1920)*4;
                    Require(renderer.RtxVideoRequested,"NV12 could not use Direct VSR processing.");
                    for (int c=0;c<3;c++) Require(Math.Abs(pixels[center+c]-expected[center+c])<=5,"NV12 range/matrix conversion changed gray levels.");
                }
                checks["rtxRgbNv12FlatColorAndRange"] = "pass";
            }
            renderer.Draw(frame,0,PreviewUpscaler.Enhanced); Require(!renderer.RtxVideoRequested && renderer.RtxVideoSurfaceBytes==0,"Switching to Normal retained RTX resources.");
            renderer.Resize(2560,1440); renderer.Draw(frame,0,PreviewUpscaler.Enhanced); Require(normal.AsSpan().SequenceEqual(renderer.Readback()),"Normal pixels changed after RTX mode.");
            renderer.Resize(1280,720); renderer.Draw(frame,0,PreviewUpscaler.RtxVideo);
            Require(!renderer.RtxVideoRequested && renderer.RtxVideoSurfaceBytes==0,"Native-size preview ran RTX upscaling or retained resources.");
        }
        foreach (bool bltFailure in new[] { false,true })
        {
            using var renderer = new D3DPreview(0,1920,1080);
            renderer.Draw(frame,.25,PreviewUpscaler.Enhanced); var normal=renderer.Readback();
            renderer.ForceRtxVideoUnavailable=!bltFailure; renderer.ForceRtxVideoBltUnavailable=bltFailure;
            renderer.Draw(frame,.25,PreviewUpscaler.RtxVideo);
            Require(!renderer.RtxVideoRequested && renderer.UpscalingFallbackReason!=null && renderer.RtxVideoSurfaceBytes==0 && normal.AsSpan().SequenceEqual(renderer.Readback()),"RTX failure did not clean up and preserve Normal output.");
            renderer.ForceRtxVideoUnavailable=false; renderer.ForceRtxVideoBltUnavailable=false;
            renderer.Draw(frame,.25,PreviewUpscaler.Enhanced); renderer.Draw(frame,.25,PreviewUpscaler.RtxVideo); renderer.Readback();
        }
        using (var renderer = new D3DPreview(0,1920,1080) { ForceRtxVideoUnavailable=true, ForceUpscalingUnavailable=true })
        {
            renderer.Draw(frame,.25,PreviewUpscaler.Standard); var standard=renderer.Readback(); renderer.Draw(frame,.25,PreviewUpscaler.RtxVideo);
            Require(!renderer.RtxVideoRequested && !renderer.UsedEnhancedUpscaling && standard.AsSpan().SequenceEqual(renderer.Readback()),"Double fallback did not preserve Standard pixels.");
        }
        if ((bool)checks["rtxSdkEvaluationSucceeded"])
        {
            using var renderer = new D3DPreview(0,2560,1440) { ForceRtxVideoUnavailable=true };
            renderer.Draw(frame,0,PreviewUpscaler.RtxVideo); Require(!renderer.RtxVideoRequested,"Forced failure did not fall back.");
            renderer.ForceRtxVideoUnavailable=false; renderer.Draw(frame,0,PreviewUpscaler.RtxVideo);
            Require(!renderer.RtxVideoRequested && renderer.RtxVideoSurfaceBytes==0,"An unchanged unsupported configuration retried every frame.");
            renderer.Resize(1920,1080); renderer.Draw(frame,0,PreviewUpscaler.RtxVideo); renderer.Readback();
            Require(renderer.RtxVideoRequested,"Changing geometry did not recover from a prior feature failure."); checks["rtxRetryAfterResize"]="pass";
        }
        checks["rtxFailureFallbackAndResourceRelease"]="pass"; checks["normalPixelsAfterRtxToggle"]="identical";
        return checks;
    }
    internal static async Task<Dictionary<string,object>> RunUi(string folder)
    {
        Directory.CreateDirectory(folder); var checks=new Dictionary<string,object>(); var source=new FrameGenerationTestSource(15);
        var main=new MainWindow(false,false,(_,_)=>new CaptureStream(()=>source,TimeSpan.FromSeconds(2))) { ShowActivated=false }; main.Show();
        async Task Wait(Func<bool> condition)
        { var timer=Stopwatch.StartNew(); while (!condition()) { if (timer.ElapsedMilliseconds>12000) throw new TimeoutException("RTX preview test timed out: "+main.DiagnosticPreview?.UpscalingFallbackReason); await Task.Delay(10); } }
        async Task Ready() => await Wait(()=>
        {
            var host=main.DiagnosticPreview; if (host==null || host.Upscaler!=PreviewUpscaler.RtxVideo) return false;
            var dpi=VisualTreeHelper.GetDpi(host);
            return host.RtxVideoRequested || host.UpscalingFallbackReason!=null || host.ActualWidth*dpi.DpiScaleX<=640.5 || host.ActualHeight*dpi.DpiScaleY<=360.5;
        });
        try
        {
            main.DiagnosticApplySettingsJson(JsonSerializer.Serialize(new { Folder=folder, Resolution=0, Bitrate=1, Sharpness=.25, Upscaling=99 }));
            Require(main.DiagnosticUpscaler==PreviewUpscaler.Standard,"Invalid upscaling preference did not retain Standard.");
            main.DiagnosticSelectUpscaler(PreviewUpscaler.RtxVideo); string saved=main.DiagnosticSettingsJson;
            main.DiagnosticSelectUpscaler(PreviewUpscaler.Enhanced); main.DiagnosticApplySettingsJson(saved);
            Require(main.DiagnosticUpscaler==PreviewUpscaler.RtxVideo && main.DiagnosticEmbedded.Upscaler==PreviewUpscaler.RtxVideo,"RTX preference did not restore.");
            main.DiagnosticPrepareInput(); await main.DiagnosticConnectFullscreen(); await Ready();
            checks["liveRtxSdkEvaluated"]=main.DiagnosticPreview!.RtxVideoRequested;
            var fpsBuckets=new List<double>();var generatedBuckets=new List<double>();
            async Task Sample(int factor)
            {
                await Ready(); await Wait(()=>main.DiagnosticPreview?.FrameGenerationActive==true); await Task.Delay(200);
                var host=main.DiagnosticPreview!; long count=0,generated=0;
                void Count(PresentedFrame frame) {Interlocked.Increment(ref count);if(frame.Kind==PresentationKind.Interpolated)Interlocked.Increment(ref generated);}
                host.FramePresented+=Count;
                try { var timer=Stopwatch.StartNew(); await Task.Delay(1000); double fps=Interlocked.Read(ref count)/timer.Elapsed.TotalSeconds; fpsBuckets.Add(fps);double generatedFps=Interlocked.Read(ref generated)/timer.Elapsed.TotalSeconds;generatedBuckets.Add(generatedFps);Require(generatedFps>15*(factor-1)*.5,"RTX plus generation only repeated frames."); Require(fps>15*factor*.75,"RTX plus frame generation stalled at factor "+factor+": "+fps); }
                finally { host.FramePresented-=Count; }
            }
            for (int cycle=0;cycle<6;cycle++)
            {
                int factor=2+cycle%3; main.DiagnosticSelectFrameGeneration((FrameGenerationMode)(factor-1));
                await main.DiagnosticWindowed(); await Sample(factor);
                await main.DiagnosticFullscreen(); main.DiagnosticMaximizeFullscreen(); await Sample(factor);
                main.DiagnosticSelectUpscaler(PreviewUpscaler.Enhanced); await Wait(()=>main.DiagnosticPreview?.RtxVideoRequested==false);
                Require(main.DiagnosticEmbedded.Upscaler==PreviewUpscaler.Enhanced,"Normal did not reach the retained windowed host.");
                main.DiagnosticSelectUpscaler(PreviewUpscaler.RtxVideo); await Ready();
            }
            checks["rtxFrameGenerationFullscreenTrips"]=6; checks["rtxPresentationOutputFps"]=fpsBuckets;checks["rtxActualGeneratedFps"]=generatedBuckets;
            await main.DiagnosticRecord(folder); var recorder=main.DiagnosticRecorder ?? throw new InvalidOperationException("RTX source recording did not start."); await Task.Delay(1100); await main.DiagnosticRecord(folder);
            Require(recorder.VideoSamplesWritten>=10 && recorder.VideoSamplesWritten<=23,"RTX processing changed the source recording rate."); checks["rtxSourceRecordingSamples"]=recorder.VideoSamplesWritten;
            await main.DiagnosticWindowed(); await Ready(); await Task.Delay(200); main.UpdateLayout();
            var image=new RenderTargetBitmap((int)Math.Ceiling(main.ActualWidth),(int)Math.Ceiling(main.ActualHeight),96,96,PixelFormats.Pbgra32); image.Render(main);
            var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using (var file=File.Create(Path.Combine(folder,"interface.png"))) encoder.Save(file);
            main.DiagnosticStaleFailure(main.DiagnosticSession); await Wait(()=>!main.DiagnosticConnected && !main.DiagnosticBusy); await main.DiagnosticDisconnect();
            checks["rtxPreferencesSwitchFullscreenRecordingDisconnect"]="pass";
        }
        finally { await main.DiagnosticClose().WaitAsync(TimeSpan.FromSeconds(5)); }
        return checks;
    }
}