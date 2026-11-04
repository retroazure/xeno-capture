using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace XenoCapture.Engine;
internal static class AiVideoDiagnostics
{
    static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    internal static async Task Run(string folder)
    {
        Directory.CreateDirectory(folder);var checks=Array.IndexOf(Environment.GetCommandLineArgs(),"--ui-only")>=0?new Dictionary<string,object>():await Task.Run(()=>RunGpu(folder));
        await File.WriteAllTextAsync(Path.Combine(folder,"gpu-results.json"),JsonSerializer.Serialize(checks,new JsonSerializerOptions{WriteIndented=true}));
        foreach(var item in await RunUi(folder))checks[item.Key]=item.Value;
        checks["result"]="PASS";await File.WriteAllTextAsync(Path.Combine(folder,"results.json"),JsonSerializer.Serialize(checks,new JsonSerializerOptions{WriteIndented=true}));
    }
    internal static Dictionary<string,object> RunGpu(string folder)
    {
        Directory.CreateDirectory(folder);using var apartment=new Apartment();var checks=new Dictionary<string,object>();
        void Stage(string value){File.WriteAllText(Path.Combine(folder,"stage.txt"),value);File.WriteAllText(Path.Combine(folder,"gpu-stage.json"),JsonSerializer.Serialize(checks,new JsonSerializerOptions{WriteIndented=true}));}
        using var frame=FrameGenerationDiagnostics.Pattern(0);using var renderer=new D3DPreview(0,1280,720);
        Require(!renderer.AiVideo.Enabled&&renderer.RtxVideoSurfaceBytes==0,"AI filter was enabled or allocated before opt-in.");
        foreach(var mode in new[]{PreviewUpscaler.Standard,PreviewUpscaler.Enhanced,PreviewUpscaler.RtxVideo})
        {
            Stage("legacy "+mode);renderer.Draw(frame,0,mode);var original=renderer.Readback();
            renderer.AiVideo=new(true,1440,3);renderer.Draw(frame,0,mode);renderer.Readback();
            Require(renderer.AiVideoActive,"AI filter could not override the existing mode: "+renderer.AiVideoFallbackReason);
            Require(renderer.AiVideoOutputWidth==2560&&renderer.AiVideoOutputHeight==1440,"AI output followed the smaller preview instead of the explicit target.");
            renderer.AiVideo=AiVideoSettings.Off;renderer.Draw(frame,0,mode);
            Require(!renderer.AiVideoActive&&original.AsSpan().SequenceEqual(renderer.Readback()),"Disabling AI did not restore the exact selected-filter pixels.");
        }
        checks["aiOffRestoresAllThreeLegacyFilters"]="pixel identical";
        var cases=new List<object>();long loadedFeatureCount=0;
        foreach(int height in new[]{720,1080,1440,2160})
        {
            Stage("resolution "+height);renderer.AiVideo=new(true,height,3);var timer=Stopwatch.StartNew();renderer.Draw(frame,0,PreviewUpscaler.Standard);var pixels=renderer.ReadbackAiVideo();
            int width=height*16/9;Require(renderer.AiVideoActive&&renderer.AiVideoOutputWidth==width&&renderer.AiVideoOutputHeight==height&&pixels.Length==width*height*4,"Wrong AI target dimensions.");
            cases.Add(new{height,width,quality=3,sdkOutputAndReadbackMs=timer.Elapsed.TotalMilliseconds});
            if(height==720)loadedFeatureCount=renderer.VsrFeatureCreations;else Require(renderer.VsrFeatureCreations==loadedFeatureCount,"A target change unnecessarily reloaded the AI feature.");
            if(height>=1440)UpscalingDiagnostics.SavePng(Path.Combine(folder,$"ai-high-{height}.png"),pixels,width,height);
        }
        checks["aiExplicitResolutionCases"]=cases;checks["aiTargetChangesReuseLoadedFeature"]="pass";
        var qualityHashes=new Dictionary<string,string>();
        foreach(int quality in new[]{1,2,3,4})
        {
            Stage("quality "+quality);renderer.AiVideo=new(true,1440,quality);renderer.Draw(frame,0,PreviewUpscaler.Standard);var pixels=renderer.ReadbackAiVideo();
            Require(renderer.AiVideoActive&&renderer.AiVideoStatus.Contains(renderer.AiVideo.QualityName),"Selected SDK quality was not evaluated.");
            qualityHashes[renderer.AiVideo.QualityName]=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels));
        }
        checks["aiQualityOutputHashes"]=qualityHashes;
        Stage("ultra 4K evaluate");renderer.AiVideo=new(true,2160,4);renderer.Draw(frame,0,PreviewUpscaler.Standard);var ultra=renderer.ReadbackAiVideo();
        // Compare the 4K result with the app's non-AI scaler without invoking
        // the SDK's diagnostic bicubic path at the unusually large 6x ratio.
        Stage("ultra 4K Standard comparison");
        using(var standard=new D3DPreview(0,3840,2160))
        {
            standard.Draw(frame,0,PreviewUpscaler.Standard);var baseline=standard.Readback();long changed=0,error=0;
            for(int i=0;i<ultra.Length;i++){int delta=Math.Abs(ultra[i]-baseline[i]);if(delta!=0)changed++;error+=delta;}
            Require(changed>10000&&error>100000,"Ultra 4K output did not differ from non-AI scaling.");
            checks["aiUltra4kChangedBytesVsStandard"]=changed;checks["aiUltra4kAbsoluteDifferenceVsStandard"]=error;
            UpscalingDiagnostics.SavePng(Path.Combine(folder,"ai-ultra-2160.png"),ultra,3840,2160);
        }
        renderer.AiVideo=AiVideoSettings.Off;
        Stage("AI versus SDK bicubic at 2x");
        using(var input=FrameGenerationDiagnostics.Pattern(0,1280,720))
        using(var model=new D3DPreview(0,2560,1440))
        {
            model.AiVideo=new(true,1440,3);model.Draw(input,0);var enhanced=model.Readback();
            // Release the model before creating the separate baseline device.
            model.AiVideo=AiVideoSettings.Off;
            using var bicubic=new D3DPreview(0,2560,1440){DisableRtxRequestForDiagnostics=true};
            bicubic.Draw(input,0,PreviewUpscaler.RtxVideo);var baseline=bicubic.Readback();long difference=0;
            for(int i=0;i<enhanced.Length;i++)difference+=Math.Abs(enhanced[i]-baseline[i]);
            Require(difference>100000,"High-quality AI output matched the SDK's bicubic baseline.");checks["ai1440AbsoluteDifferenceVsSdkBicubic"]=difference;
        }
        Stage("aspect ratio");using(var aspect=FrameGenerationDiagnostics.Pattern(0,640,480))
        {
            renderer.AiVideo=new(true,1440,3);renderer.Draw(aspect,0);renderer.ReadbackAiVideo();
            Require(renderer.AiVideoActive&&renderer.AiVideoOutputWidth==1920&&renderer.AiVideoOutputHeight==1440,"AI filter stretched a 4:3 input.");
        }
        Stage("native size and auto");using(var native=FrameGenerationDiagnostics.Pattern(0,1280,720))
        {
            renderer.AiVideo=new(true,720,3);renderer.Draw(native,0);renderer.Readback();
            Require(renderer.AiVideoActive&&renderer.AiVideoOutputWidth==1280&&renderer.AiVideoOutputHeight==720,"Native-size AI evaluation failed: "+renderer.AiVideoFallbackReason);
            renderer.AiVideo=new(true,0,3);renderer.Draw(native,0);Require(renderer.AiVideoActive,"Auto target failed.");
        }
        Stage("NV12 colors");using(var nv12=new Packet(640*360*3/2,false,640,360,PixelLayout.Nv12))
        {
            Array.Fill(nv12.Bytes,(byte)128,0,nv12.Length);
            foreach(bool range in new[]{false,true})foreach(bool bt601 in new[]{false,true})
            {
                nv12.FullRange=range;nv12.Bt601=bt601;renderer.AiVideo=AiVideoSettings.Off;renderer.Draw(nv12,0);var expected=renderer.Readback();
                renderer.AiVideo=new(true,1440,4);renderer.Draw(nv12,0);var output=renderer.ReadbackAiVideo();
                for(int c=0;c<3;c++)Require(Math.Abs(output[(720*2560+1280)*4+c]-expected[(360*1280+640)*4+c])<=5,"AI filter changed NV12 gray range/matrix.");
            }
        }
        Stage("exact duplicate and cached-frame performance");
        renderer.AiVideo=new(true,1440,3);using(var original=FrameGenerationDiagnostics.Pattern(0))
        {
            renderer.Draw(original,0);renderer.Readback();long evaluated=renderer.VsrEvaluations;
            using var identical=FrameGenerationDiagnostics.Pattern(0);identical.Time=original.Time+333333;
            renderer.Draw(identical,0);var cached=renderer.Readback();
            Require(renderer.VsrEvaluations==evaluated&&renderer.AiDuplicateUploadsSkipped>0&&renderer.VsrOutputCacheHits>0,"An identical retained capture reran upload or inference.");
            identical.Bytes[5000]^=1;renderer.Draw(identical,0);renderer.Readback();
            Require(renderer.VsrEvaluations==evaluated+1,"A changed pixel was incorrectly treated as a duplicate.");
            renderer.AiVideo=AiVideoSettings.Off;renderer.AiVideo=new(true,1440,3);renderer.Draw(original,0);Require(cached.AsSpan().SequenceEqual(renderer.Readback()),"Cached AI output differs from fresh evaluation.");
        }
        checks["aiExactDuplicateCacheAndOnePixelInvalidation"]="pass";
        Stage("1080p steady-state performance");
        using(var a=FrameGenerationDiagnostics.Pattern(0,1920,1080))using(var b=FrameGenerationDiagnostics.Pattern(12,1920,1080))
        using(var ac=new Packet(a.Length,false,1920,1080))using(var bc=new Packet(b.Length,false,1920,1080))
        {
            a.Bytes.AsSpan(0,a.Length).CopyTo(ac.Bytes);b.Bytes.AsSpan(0,b.Length).CopyTo(bc.Bytes);var workload=new List<object>();
            foreach(int target in new[]{1440,2160})
            {
                renderer.AiVideo=new(true,target,3);
                for(int i=0;i<6;i++)renderer.Draw(i%2==0?a:b,0);renderer.Readback();
                long count=renderer.VsrEvaluations;var clock=Stopwatch.StartNew();
                for(int i=0;i<24;i++)renderer.Draw(i%2==0?a:b,0);renderer.Readback();double unique=clock.Elapsed.TotalMilliseconds/24;
                Require(renderer.VsrEvaluations-count==24,"Distinct moving frames were not all enhanced.");
                count=renderer.VsrEvaluations;long hits=renderer.VsrOutputCacheHits;clock.Restart();
                for(int i=0;i<24;i++)renderer.Draw((i%4) switch{0=>a,1=>ac,2=>b,_=>bc},0);renderer.Readback();double duplicate=clock.Elapsed.TotalMilliseconds/24;
                long evaluated=renderer.VsrEvaluations-count;Require(evaluated==12&&renderer.VsrOutputCacheHits-hits==12,"30-in-60 workload did not avoid exactly the repeated inference work.");
                workload.Add(new{input="1920x1080",targetHeight=target,quality="High",distinctFramesMeanMs=unique,duplicateCaptureMeanMs=duplicate,inferencesFor24DuplicateCapturePackets=evaluated,inferenceCacheHits=renderer.VsrOutputCacheHits-hits});
            }
            checks["ai1080pSteadyPerformance"]=workload;
        }
        Stage("AI display resampling");
        checks["aiDisplayResampling"] = CheckDisplayResampling();
        Stage("AI direct generated input");
        renderer.AiVideo = AiVideoSettings.Off;
        checks["aiGeneratedDirectInput"] = CheckDirectGeneratedInput(renderer);
        Stage("fallback and retry");renderer.AiVideo=AiVideoSettings.Off;renderer.Draw(frame,0);var fallback=renderer.Readback();renderer.ForceRtxVideoUnavailable=true;
        renderer.AiVideo=new(true,1440,3);renderer.Draw(frame,0);
        Require(!renderer.AiVideoActive&&renderer.AiVideoFallbackReason!=null&&renderer.RtxVideoSurfaceBytes==0&&fallback.AsSpan().SequenceEqual(renderer.Readback()),"AI failure did not preserve the existing Standard output.");
        renderer.ForceRtxVideoUnavailable=false;renderer.Draw(frame,0);Require(!renderer.AiVideoActive,"Failed configuration retried every frame.");
        renderer.AiVideo=new(true,1440,4);renderer.Draw(frame,0);Require(renderer.AiVideoActive,"Changed quality did not recover a failed request.");
        renderer.AiVideo=AiVideoSettings.Off;Require(renderer.RtxVideoSurfaceBytes==0&&!renderer.AiVideoActive,"Disabling without another video packet retained AI surfaces.");
        var valid=AiVideoSettings.Validate(new(true,int.MaxValue,int.MinValue));Require(valid.OutputHeight==0&&valid.Quality==3,"Invalid AI preferences bypassed validation.");
        checks["aiAspectNativeAutoNv12FailureRetryAndIdleRelease"]="pass";return checks;
    }
    static object CheckDisplayResampling()
    {
        const int size = 96;
        using var frame = new Packet(size * size * 4, false, size, size);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int i = (y * size + x) * 4;
                byte value = (byte)((x & 1) == 0 ? 16 : 240);
                frame.Bytes[i] = frame.Bytes[i + 1] = frame.Bytes[i + 2] = value;
                frame.Bytes[i + 3] = 255;
            }
        using var renderer = new D3DPreview(0, 64, 64);
        renderer.Draw(frame, 0);
        byte[] linear = renderer.Readback();
        renderer.DiagnosticAiResampling = true;
        renderer.Draw(frame, 0);
        byte[] area = renderer.Readback();
        double linearError = 0, areaError = 0;
        for (int x = 0; x < 64; x++)
        {
            double lo = x * 1.5, hi = lo + 1.5, expected = 0;
            for (int texel = (int)lo; texel < hi; texel++)
                expected += (Math.Min(hi, texel + 1) - Math.Max(lo, texel)) * ((texel & 1) == 0 ? 16 : 240) / 1.5;
            int i = (32 * 64 + x) * 4;
            linearError += Math.Abs(linear[i] - expected);
            areaError += Math.Abs(area[i] - expected);
        }
        Require(areaError < linearError * .15, "AI output reduction did not preserve area coverage.");

        renderer.Resize(192, 192);
        renderer.DiagnosticAiResampling = false;
        renderer.Draw(frame, 0);
        linear = renderer.Readback();
        renderer.DiagnosticAiResampling = true;
        renderer.Draw(frame, 0);
        byte[] detail = renderer.Readback();
        int linearContrast = 0, detailContrast = 0;
        for (int x = 2; x < 190; x++)
        {
            int i = (96 * 192 + x) * 4;
            linearContrast += Math.Abs(linear[i] - 128);
            detailContrast += Math.Abs(detail[i] - 128);
            Require(detail[i] is >= 16 and <= 240, "AI display enlargement introduced a halo.");
        }
        Require(detailContrast > linearContrast * 1.10, "AI display enlargement did not preserve line contrast.");
        return new { linearError, areaError, linearContrast, detailContrast };
    }

    static object CheckDirectGeneratedInput(D3DPreview renderer)
    {
        using var first = FrameGenerationDiagnostics.Pattern(0);
        using var second = FrameGenerationDiagnostics.Pattern(16);
        first.Time = 1_000_000;
        second.Time = first.Time + first.Duration;
        renderer.DisableFrameBudgetForDiagnostics = true;
        renderer.AiVideo = new(true, 720, 3);
        Require(renderer.PrepareInterpolation(first) && renderer.PrepareInterpolation(second), "The AI motion fixture failed to prepare.");
        renderer.DisableVsrDirectInputForDiagnostics = true;
        Require(renderer.RenderMidpoint(0, PreviewUpscaler.Standard).Kind == PresentationKind.Interpolated, "The AI motion fixture did not generate a frame.");
        byte[] converted = renderer.ReadbackAiVideo();
        renderer.DisableVsrDirectInputForDiagnostics = false;
        long directInputs = renderer.VsrDirectInputs;
        renderer.RenderMidpoint(0, PreviewUpscaler.Standard);
        byte[] direct = renderer.ReadbackAiVideo();
        Require(renderer.VsrDirectInputs == directInputs + 1 && converted.AsSpan().SequenceEqual(direct),
            "Skipping the generated-frame RGB copy changed AI output or bypassed the model.");
        renderer.AiVideo = AiVideoSettings.Off;
        renderer.ResetInterpolation();
        renderer.DisableFrameBudgetForDiagnostics = false;
        return "pixel identical";
    }

    internal static async Task<Dictionary<string,object>> RunUi(string folder)
    {
        var checks=new Dictionary<string,object>();var source=new FrameGenerationTestSource(30);
        var main=new MainWindow(false,false,(_,_)=>new CaptureStream(()=>source,TimeSpan.FromSeconds(2))){ShowActivated=false};main.Show();
        async Task Wait(Func<bool> ready){var timer=Stopwatch.StartNew();while(!ready()){if(timer.ElapsedMilliseconds>15000)throw new TimeoutException("AI preview check: "+main.DiagnosticPreview?.AiVideoStatus+" / "+main.DiagnosticPreview?.AiVideoFallbackReason+" / "+main.DiagnosticPreviewError);await Task.Delay(10);}}
        void Stage(string text)=>File.WriteAllText(Path.Combine(folder,"ui-stage.txt"),text);
        try
        {
            Stage("AI settings and connect");main.DiagnosticApplySettingsJson(JsonSerializer.Serialize(new{Folder=folder,Resolution=0,Bitrate=1,Sharpness=0,Upscaling=1}));
            Require(!main.DiagnosticAiVideo.Enabled&&!main.DiagnosticEmbedded.AiVideo.Enabled&&!main.DiagnosticAiControlsEnabled,"Old preferences enabled the new filter.");
            main.DiagnosticSelectAiVideo(true,1440,3);string saved=main.DiagnosticSettingsJson;main.DiagnosticSelectAiVideo(false);main.DiagnosticApplySettingsJson(saved);
            Require(main.DiagnosticAiVideo==new AiVideoSettings(true,1440,3)&&main.DiagnosticEmbedded.AiVideo==main.DiagnosticAiVideo&&main.DiagnosticUpscaler==PreviewUpscaler.Enhanced,"AI preferences did not restore separately.");
            main.DiagnosticPrepareInput();await main.DiagnosticConnectFullscreen();
            await Wait(()=>main.DiagnosticPreview?.AiVideoActive==true&&main.DiagnosticPreview.AiVideoStatus.Contains("2560×1440"));
            main.DiagnosticSelectAiVideo(true,2160,4);await Wait(()=>main.DiagnosticPreview?.AiVideoStatus.Contains("Ultra · 3840×2160")==true);
            Stage("return to retained preview");await main.DiagnosticWindowed();await Wait(()=>main.DiagnosticPreview?.AiVideoActive==true&&main.DiagnosticPreview.AiVideoStatus.Contains("3840×2160"));
            // Enabling/disabling while no packets arrive must still publish the new configuration.
            Stage("paused input disable and resume");long beforeResume=main.DiagnosticPresented;source.PauseFrames=true;main.DiagnosticSelectAiVideo(false);await Wait(()=>main.DiagnosticPreview?.AiVideoStatus=="Off"&&!main.DiagnosticPreview.AiVideoActive);source.PauseFrames=false;
            await Wait(()=>main.DiagnosticPresented>beforeResume&&main.DiagnosticPreview?.Upscaler==PreviewUpscaler.Enhanced&&!main.DiagnosticPreview.AiVideoActive);
            Stage("AI with frame generation");main.DiagnosticSelectAiVideo(true,720,2);main.DiagnosticSelectFrameGeneration(FrameGenerationMode.Adaptive2x);
            await Wait(()=>main.DiagnosticPreview?.AiVideoActive==true&&main.DiagnosticPreview.FrameGenerationActive);
            var host=main.DiagnosticPreview!;await Task.Delay(300);long before=host.GeneratedPresentations;await Task.Delay(1200);
            Require(host.GeneratedPresentations-before>15,"AI preview plus generation only repeated frames.");
            long warmFeatureCount = host.VsrFeatureCreations;
            var enteringMs = new List<double>();
            var leavingMs = new List<double>();
            for(int cycle=0;cycle<6;cycle++)
            {
                Stage("AI fullscreen round trip "+(cycle+1));
                var transition = Stopwatch.StartNew();
                await main.DiagnosticFullscreen();main.DiagnosticMaximizeFullscreen();await Wait(()=>main.DiagnosticPreview?.AiVideoActive==true&&main.DiagnosticPreview.FrameGenerationActive);
                enteringMs.Add(transition.Elapsed.TotalMilliseconds);
                Require(main.DiagnosticPreview!.VsrFeatureCreations == warmFeatureCount, "Entering fullscreen reloaded the NVIDIA feature.");
                transition.Restart();
                await main.DiagnosticWindowed();await Wait(()=>main.DiagnosticPreview?.AiVideoActive==true&&main.DiagnosticPreview.FrameGenerationActive&&main.DiagnosticPreview.AiVideoStatus.Contains("1280×720"));
                leavingMs.Add(transition.Elapsed.TotalMilliseconds);
                Require(main.DiagnosticPreview!.VsrFeatureCreations == warmFeatureCount, "Leaving fullscreen reloaded the NVIDIA feature.");
            }
            checks["aiEnterFullscreenMs"] = enteringMs;
            checks["aiLeaveFullscreenMs"] = leavingMs;
            Stage("auto target fullscreen handoff");
            main.DiagnosticSelectAiVideo(true, 0, 2);
            await Wait(() => main.DiagnosticPreview?.AiVideoActive == true && main.DiagnosticPreview.AiVideoStatus.Contains("Balanced"));
            var auto = Stopwatch.StartNew();
            await main.DiagnosticFullscreen(); main.DiagnosticMaximizeFullscreen();
            await Wait(() => main.DiagnosticPreview?.AiVideoActive == true);
            checks["aiAutoEnterFullscreenMs"] = auto.Elapsed.TotalMilliseconds;
            Require(main.DiagnosticPreview!.VsrFeatureCreations == warmFeatureCount, "Auto fullscreen reloaded the NVIDIA feature.");
            auto.Restart();
            await main.DiagnosticWindowed();
            checks["aiAutoLeaveFullscreenHandoffMs"] = auto.Elapsed.TotalMilliseconds;
            await Wait(() => main.DiagnosticPreview?.AiVideoActive == true);
            checks["aiAutoLeaveFullscreenMs"] = auto.Elapsed.TotalMilliseconds;
            Require(auto.Elapsed.TotalMilliseconds < 1_000, "Auto AI fullscreen return stalled during swap-chain resize.");
            checks["aiAutoReturnFirstDrawMs"] = main.DiagnosticPreview!.LastSubmissionMs;
            Require(main.DiagnosticPreview!.VsrFeatureCreations == warmFeatureCount, "Auto windowed preview reloaded the NVIDIA feature.");
            await main.DiagnosticRecord(folder);var recorder=main.DiagnosticRecorder??throw new InvalidOperationException("AI recording independence check did not start.");await Task.Delay(1100);await main.DiagnosticRecord(folder);
            Require(recorder.VideoSamplesWritten is >=23 and <=42,"AI preview changed the recording's source sample rate.");checks["aiSourceRecordingSamples"]=recorder.VideoSamplesWritten;
            await Task.Delay(200);main.UpdateLayout();var image=new RenderTargetBitmap((int)Math.Ceiling(main.ActualWidth),(int)Math.Ceiling(main.ActualHeight),96,96,PixelFormats.Pbgra32);image.Render(main);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using(var file=File.Create(Path.Combine(folder,"interface.png")))png.Save(file);
            main.DiagnosticStaleFailure(main.DiagnosticSession);await Wait(()=>!main.DiagnosticConnected&&!main.DiagnosticBusy);await main.DiagnosticDisconnect();
            main.DiagnosticSelectAiVideo(false);await Wait(()=>main.DiagnosticEmbedded.AiVideoStatus=="Off"&&!main.DiagnosticEmbedded.AiVideoActive);
            checks["aiDefaultOffSeparatePreferencesFixedTargetsPauseToggleGenerationSixFullscreenRecordingDisconnect"]="pass";
        }
        finally{source.PauseFrames=false;await main.DiagnosticClose().WaitAsync(TimeSpan.FromSeconds(6));}
        return checks;
    }
}
