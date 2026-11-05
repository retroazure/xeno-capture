using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XenoCapture.Engine;
internal static class CappedGameDiagnostics
{
    static void Require(bool condition,string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static async Task Run(string folder,int cycles=6)
    {
        Directory.CreateDirectory(folder); var checks=await Task.Run(()=>RunGpu(folder));
        await File.WriteAllTextAsync(Path.Combine(folder,"gpu-results.json"),JsonSerializer.Serialize(checks,new JsonSerializerOptions { WriteIndented=true }));
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--gpu-only") < 0)
            foreach (var item in await RunUi(folder,cycles)) checks[item.Key]=item.Value;
        checks["result"]="PASS"; await File.WriteAllTextAsync(Path.Combine(folder,"results.json"),JsonSerializer.Serialize(checks,new JsonSerializerOptions { WriteIndented=true }));
    }
    internal static Dictionary<string,object> RunGpu(string folder)
    {
        Directory.CreateDirectory(folder); using var apartment=new Apartment(); var checks=new Dictionary<string,object>();
        const long capturePeriod=166666, gamePeriod=capturePeriod*2;
        using var a=FrameGenerationDiagnostics.Pattern(0); using var b=FrameGenerationDiagnostics.Pattern(12);
        a.Time=1_000_000; a.Duration=capturePeriod; b.Time=a.Time+gamePeriod; b.Duration=capturePeriod;
        using var renderer=new D3DPreview(0,640,360) { DisableFrameBudgetForDiagnostics=true };
        Require(renderer.PrepareInterpolation(a,gamePeriod,true),"First capped-game image failed."); long firstTime=renderer.CachedFrame.Time, bytes=renderer.FrameGenerationSurfaceBytes;
        using var repeat = FrameGenerationDiagnostics.Pattern(0);
        repeat.Time = a.Time + capturePeriod; repeat.Duration = capturePeriod;
        Require(renderer.PrepareInterpolation(repeat,gamePeriod,true) && renderer.FrameWasDuplicate
            && renderer.ExactInterpolationRepeats == 1, "The byte-identical HDMI frame reached GPU motion analysis.");
        Require(renderer.CachedFrame.Time==firstTime && !renderer.HasInterpolationPair,"A repeated capture advanced the distinct-image history.");
        Require(renderer.PrepareInterpolation(b,gamePeriod,true) && !renderer.FrameWasDuplicate && renderer.HasInterpolationPair && renderer.CanGenerateMidpoint,"Distinct 30 FPS motion did not form a valid pair: "+renderer.FrameGenerationDetail);
        Require(renderer.PreviousFrame.Time==firstTime && renderer.CachedFrame.Duration==gamePeriod,"Game timing retained the duplicated capture timestamp or duration.");
        var errors=new Dictionary<string,List<double>>();
        for (int factor=2;factor<=4;factor++)
        {
            var factorErrors=new List<double>();
            for (int step=1;step<factor;step++)
            {
                using var expected=FrameGenerationDiagnostics.Pattern(12*step/factor);
                var info=renderer.RenderIntermediate(step,factor,0,PreviewUpscaler.Standard); var pixels=renderer.Readback();
                Require(info.Kind==PresentationKind.Interpolated && info.Time==firstTime+gamePeriod*step/factor,"The game midpoint used duplicate-capture timing.");
                long difference=0,count=0; for(int y=80;y<328;y++) for(int x=40;x<600;x++) for(int c=0;c<3;c++) { difference+=Math.Abs(pixels[(y*640+x)*4+c]-expected.Bytes[(y*640+x)*4+c]); count++; }
                double error=difference/(double)count; factorErrors.Add(error); Require(error<2,"Generated game image does not match its known fractional motion: "+error);
                for(int y=30;y<68;y++) for(int x=40;x<200;x++) for(int c=0;c<3;c++) Require(pixels[(y*640+x)*4+c]==expected.Bytes[(y*640+x)*4+c],"Game-mode interpolation altered static HUD pixels.");
                UpscalingDiagnostics.SavePng(Path.Combine(folder,$"game-{factor}x-{step}.png"),pixels,640,360);
            }
            errors[factor+"x"]=factorErrors;
        }
        b.Time+=capturePeriod; Require(renderer.PrepareInterpolation(b,gamePeriod,true) && renderer.FrameWasDuplicate && renderer.CachedFrame.Time==firstTime+gamePeriod,"The second repeat replaced the real game frame.");
        Require(renderer.FrameGenerationSurfaceBytes==bytes,"Cadence mode allocated additional frame-history surfaces.");
        using(var cut=new Packet(640*360*4,false,640,360) { Time=firstTime+2*gamePeriod,Duration=capturePeriod })
        {
            Array.Fill(cut.Bytes,(byte)255,0,cut.Length); Require(renderer.PrepareInterpolation(cut,gamePeriod,true) && !renderer.CanGenerateMidpoint,"A scene cut was synthesized.");
            var info=renderer.RenderIntermediate(1,2,0,PreviewUpscaler.Standard); Require(info.Kind==PresentationKind.Repeated && b.Bytes.AsSpan(0,b.Length).SequenceEqual(renderer.Readback()),"A scene cut invented game-frame pixels.");
        }
        renderer.ResetInterpolation(); Require(renderer.FrameGenerationSurfaceBytes==0 && !renderer.FrameWasDuplicate,"Reset retained game history.");
        Require(FrameGenerationPolicy.Content30Period(166667)==333334 && FrameGenerationPolicy.Content30Period(166833)==333666 && FrameGenerationPolicy.Content30Period(333333)==333333,"30/29.97 game timing did not preserve capture clock ratios.");
        for(int factor=2;factor<=4;factor++)
        {
            Require(FrameGenerationPolicy.CanGenerate(FrameGenerationPolicy.Content30Period(166666),30*factor,1920,1080,factor),"A supported game-content target was rejected.");
            Require(!FrameGenerationPolicy.CanGenerate(166666,60,1920,1080,factor),"The normal capture policy unexpectedly bypassed its refresh guard.");
        }
        using(var wide=new D3DPreview(0,640,360) { DisableFrameBudgetForDiagnostics=true })
        using(var start=FrameGenerationDiagnostics.Pattern(0)) using(var finish=FrameGenerationDiagnostics.Pattern(32)) using(var expected=FrameGenerationDiagnostics.Pattern(16))
        {
            start.Time=2_000_000; finish.Time=start.Time+gamePeriod; start.Duration=finish.Duration=capturePeriod;
            Require(wide.PrepareInterpolation(start,gamePeriod,true) && wide.PrepareInterpolation(finish,gamePeriod,true) && wide.CanGenerateMidpoint,"Larger capped-game movement was not accepted.");
            wide.RenderMidpoint(0,PreviewUpscaler.Standard); var pixels=wide.Readback(); long difference=0,count=0;
            for(int y=80;y<328;y++) for(int x=40;x<600;x++) for(int c=0;c<3;c++) { difference+=Math.Abs(pixels[(y*640+x)*4+c]-expected.Bytes[(y*640+x)*4+c]); count++; }
            double error=difference/(double)count; Require(error<2,"The wider search did not reconstruct larger known motion: "+error); checks["wideGameMotionMeanError"]=error;
        }
        using(var full=new D3DPreview(0,1920,1080) { DisableFrameBudgetForDiagnostics=true })
        using(var left=FrameGenerationDiagnostics.Pattern(0,1920,1080)) using(var right=FrameGenerationDiagnostics.Pattern(24,1920,1080))
        {
            double total=0, synthesisTotal=0; int confident=0; long time=3_000_000;
            for(int i=0;i<12;i++)
            {
                var input=i%2==0 ? left:right; input.Time=time; input.Duration=capturePeriod; time+=gamePeriod;
                Require(full.PrepareInterpolation(input,gamePeriod,true),"1080p game-mode preparation failed.");
                if(i>=4) { total+=full.FrameAnalysisMs; if(full.CanGenerateMidpoint) confident++; }
                full.RenderMidpoint(.25,PreviewUpscaler.Standard);
                if (i >= 4) synthesisTotal += full.LastUploadDrawMs;
            }
            full.Readback(); Require(confident>4,"1080p capped-game interpolation was ineffective."); checks["warm1080pCappedAnalysisMeanMs"]=total/8; checks["warm1080pCappedSynthesisAndDrawMeanMs"]=synthesisTotal/8; checks["cappedGameSurfaceBytes1080p"]=full.FrameGenerationSurfaceBytes;
        }
        checks["layeredMotionQuality"] = CheckLayeredMotion(folder, gamePeriod);
        checks["incompatibleMotionFallback"] = CheckIncompatibleMotion(gamePeriod);
        checks["localizedOcclusion"] = new {
            native = CheckLocalizedOcclusion(folder, capturePeriod, 60),
            capped = CheckLocalizedOcclusion(folder, gamePeriod, 30)
        };
        long correctionSum=0,lastClockError=0;
        for(int frameIndex=1;frameIndex<=216000;frameIndex++)
        {
            long observed=(long)Math.Round(frameIndex*10_000_000.0/30);
            long due=frameIndex*333332L+12_500+correctionSum;
            long correction=FrameGenerationPolicy.GameCadenceCorrection(observed+12_500,due,333332);
            Require(Math.Abs(correction)<=2000,"Game phase correction exceeded its pacing bound."); correctionSum+=correction;
            lastClockError=observed+12_500-(frameIndex*333332L+12_500+correctionSum);
        }
        Require(Math.Abs(lastClockError)<100,"Two-hour nominal-clock rounding accumulated game cadence drift.");
        Require(FrameGenerationPolicy.GameCadenceCorrection(1_000_000,0,333332)==0,"An unrelated timestamp jump was pulled into the phase lock.");
        Require(FrameGenerationPolicy.CadenceLead(333333, 2, 0) == 10_000
            && FrameGenerationPolicy.CadenceLead(166666, 4, 20) == 20_833,
            "The adaptive lead fell outside its timing bounds.");
        checks["twoHourNominalClockSimulationResidualMs"]=lastClockError/10000.0;
        checks["cappedGameFractionalImageMeanErrors"]=errors; checks["duplicateHistoryTimingHudCutAndMemory"]="pass"; checks["game30And2997TimingPolicy"]="pass";
        checks["gameSurfaceBytes640x360"]=bytes; return checks;
    }
    static object CheckLayeredMotion(string folder, long period)
    {
        using var renderer = new D3DPreview(0, 640, 360) { DisableFrameBudgetForDiagnostics = true };
        using var first = LayeredScene(0);
        using var last = LayeredScene(1);
        first.Time = 1_000_000;
        last.Time = first.Time + period;
        var measurements = new List<object>();
        foreach (var (step, factor) in new[] { (1, 2), (1, 3), (2, 3), (1, 4), (3, 4) })
        {
            renderer.ResetInterpolation();
            Require(renderer.PrepareInterpolation(first, period, true) && renderer.PrepareInterpolation(last, period, true),
                "Layered motion failed to prepare.");
            double phase = (double)step / factor;
            using var expected = LayeredScene(phase);
            var result = renderer.RenderIntermediate(step, factor, 0, PreviewUpscaler.Standard);
            var pixels = renderer.Readback();
            long error = 0, repeatError = 0, badPixels = 0;
            var nearest = phase <= .5 ? first : last;
            for (int y = 90; y < 300; y++)
            for (int x = 100; x < 540; x++)
            {
                int offset = (y * 640 + x) * 4;
                int worst = 0;
                for (int c = 0; c < 3; c++)
                {
                    int delta = Math.Abs(pixels[offset + c] - expected.Bytes[offset + c]);
                    worst = Math.Max(worst, delta);
                    error += delta;
                    repeatError += Math.Abs(nearest.Bytes[offset + c] - expected.Bytes[offset + c]);
                }
                if (worst >= 48) badPixels++;
            }
            for (int y = 26; y < 52; y++)
                Require(pixels.AsSpan((y * 640 + 42) * 4, 138 * 4).SequenceEqual(expected.Bytes.AsSpan((y * 640 + 42) * 4, 138 * 4)),
                    "Layered motion changed the static HUD.");
            if (renderer.HardwareOpticalFlowActive && factor <= 3)
                Require(result.Kind == PresentationKind.Repeated && renderer.FrameGenerationDetail == "Uncertain motion fallback"
                    && pixels.AsSpan().SequenceEqual(first.Bytes.AsSpan(0, first.Length)),
                    $"Layered motion produced an unsafe image at {factor}x phase {step}.");
            else if (renderer.HardwareOpticalFlowActive)
                Require(result.Kind == PresentationKind.Interpolated && error < repeatError * .4 && badPixels < 4500,
                    $"Layered motion showed excessive distortion at {factor}x phase {step}: {badPixels} high-error pixels.");
            measurements.Add(new { factor, step, phase, hardware = renderer.HardwareOpticalFlowActive,
                generated = result.Kind == PresentationKind.Interpolated, badPixels,
                meanError = error / (440.0 * 210 * 3), repeatMeanError = repeatError / (440.0 * 210 * 3),
                generatedFraction = renderer.GeneratedPixelFraction,
                unmatchedHighContrastFraction = renderer.UnmatchedHighContrastFraction });
            UpscalingDiagnostics.SavePng(Path.Combine(folder, $"layered-{factor}x-{step}.png"), pixels, 640, 360);
            UpscalingDiagnostics.SavePng(Path.Combine(folder, $"layered-{factor}x-{step}-expected.png"), expected.Bytes, 640, 360);
        }
        return measurements;
    }

    static string CheckIncompatibleMotion(long period)
    {
        using var renderer = new D3DPreview(0, 640, 360) { DisableFrameBudgetForDiagnostics = true };
        using var first = LayeredScene(0);
        using var last = LayeredScene(0);
        // A large overlay changes most of the image while the surrounding
        // pixels still provide confident zero-motion vectors.
        for (int y = 55; y < 340; y++)
        for (int x = 50; x < 590; x++)
        for (int channel = 0; channel < 3; channel++)
            last.Bytes[(y * 640 + x) * 4 + channel] ^= 112;
        first.Time = 1_000_000;
        last.Time = first.Time + period;
        foreach (int factor in new[] { 2, 3, 4 })
        {
            renderer.ResetInterpolation();
            Require(renderer.PrepareInterpolation(first, period, true) && renderer.PrepareInterpolation(last, period, true),
                "Incompatible motion failed to prepare.");
            if (!renderer.HardwareOpticalFlowActive) return "NVIDIA optical flow unavailable";
            Require(renderer.CanGenerateMidpoint, "The fixture did not reach synthesis: " + renderer.FrameGenerationDetail);
            var info = renderer.RenderIntermediate(1, factor, 0, PreviewUpscaler.Standard);
            Require(info.Kind == PresentationKind.Repeated && !renderer.CanGenerateMidpoint
                && renderer.FrameGenerationDetail == "Uncertain motion fallback"
                && first.Bytes.AsSpan(0, first.Length).SequenceEqual(renderer.Readback()),
                $"Incompatible motion showed synthesized pixels at {factor}x: {renderer.FrameGenerationDetail}");
            if (factor > 2)
                Require(renderer.RenderIntermediate(factor - 1, factor, 0, PreviewUpscaler.Standard).Kind == PresentationKind.Repeated,
                    $"A later phase retried a rejected {factor}x pair.");
        }
        return "Uncertain motion fallback";
    }

    static object CheckLocalizedOcclusion(string folder, long period, int rate)
    {
        using var renderer = new D3DPreview(0, 640, 360) { DisableFrameBudgetForDiagnostics = true };
        using var first = LayeredScene(0, stationaryCamera: true);
        using var last = LayeredScene(1, stationaryCamera: true);
        first.Time = 1_000_000;
        last.Time = first.Time + period;
        var measurements = new List<object>();
        foreach (var (step, factor) in new[] { (1, 2), (1, 3), (2, 3) })
        {
            renderer.ResetInterpolation();
            Require(renderer.PrepareInterpolation(first, period) && renderer.PrepareInterpolation(last, period),
                "Localized occlusion failed to prepare.");
            if (!renderer.HardwareOpticalFlowActive) return "NVIDIA optical flow unavailable";

            renderer.DisableBackgroundRecoveryForDiagnostics = true;
            var baseline = renderer.RenderIntermediate(step, factor, 0, PreviewUpscaler.Standard);
            var before = renderer.Readback();
            renderer.DisableBackgroundRecoveryForDiagnostics = false;
            var recovered = renderer.RenderIntermediate(step, factor, 0, PreviewUpscaler.Standard);
            var after = renderer.Readback();
            Require(baseline.Kind == PresentationKind.Interpolated && recovered.Kind == PresentationKind.Interpolated,
                $"Localized occlusion stopped generating at {rate} fps and {factor}x phase {step}.");

            using var expected = LayeredScene((double)step / factor, stationaryCamera: true);
            var beforeError = MeasureSilhouette(before, expected.Bytes);
            var afterError = MeasureSilhouette(after, expected.Bytes);
            Require(afterError.Trailing < beforeError.Trailing && afterError.Missing <= beforeError.Missing,
                $"Background recovery left more silhouette error at {rate} fps and {factor}x phase {step}: {beforeError} to {afterError}.");
            measurements.Add(new { factor, step, trailingBefore = beforeError.Trailing, trailingAfter = afterError.Trailing,
                missingBefore = beforeError.Missing, missingAfter = afterError.Missing });
            UpscalingDiagnostics.SavePng(Path.Combine(folder, $"localized-{rate}-{factor}x-{step}.png"), after, 640, 360);
        }
        return measurements;
    }

    static (int Trailing, int Missing) MeasureSilhouette(byte[] pixels, byte[] expected)
    {
        static bool Foreground(byte[] data, int i) => data[i + 2] > 180 && data[i] < 70 && data[i + 1] < 70;

        int trailing = 0, missing = 0;
        for (int y = 120; y < 244; y++)
        for (int x = 205; x < 350; x++)
        {
            int offset = (y * 640 + x) * 4;
            bool found = Foreground(pixels, offset), wanted = Foreground(expected, offset);
            if (found && !wanted) trailing++;
            if (!found && wanted) missing++;
        }
        return (trailing, missing);
    }
    static Packet LayeredScene(double phase, bool stationaryCamera = false)
    {
        var frame = new Packet(640 * 360 * 4, false, 640, 360) { Duration = 166666 };
        for (int y = 0; y < 360; y++)
        for (int x = 0; x < 640; x++)
        {
            double cameraPhase = stationaryCamera ? 0 : phase;
            double worldX = x + 24 * cameraPhase, worldY = y + 8 * cameraPhase;
            double localX = x - 36 * phase, localY = y + 12 * phase;
            bool foreground = localX >= 220 && localX < 304 && localY >= 138 && localY < 240;
            int offset = (y * 640 + x) * 4;
            for (int c = 0; c < 3; c++)
            {
                double value = foreground
                    ? (c == 2 ? 209 : 44) + 24 * Math.Sin(localX * .25 + localY * .12 + c)
                    : 112 + 48 * Math.Sin(worldX * .065 + worldY * .026 + c)
                          + 39 * Math.Cos(worldY * .115 - worldX * .038 + c * 2);
                if (x >= 42 && x < 180 && y >= 26 && y < 52)
                    value = x % 11 < 4 && y >= 32 && y < 46 ? 244 : 16;
                frame.Bytes[offset + c] = (byte)Math.Round(value);
            }
            frame.Bytes[offset + 3] = 255;
        }
        return frame;
    }

    internal static async Task<Dictionary<string,object>> RunUi(string folder,int cycles=4)
    {
        Directory.CreateDirectory(folder); var checks=new Dictionary<string,object>(); var source=new FrameGenerationTestSource(60,30);
        var main=new MainWindow(false,false,(_,_)=>new CaptureStream(()=>source,TimeSpan.FromSeconds(2))) { ShowActivated=false }; main.Show();
        async Task Wait(Func<bool> condition)
        { var timer=Stopwatch.StartNew(); while(!condition()) { if(timer.ElapsedMilliseconds>10000) throw new TimeoutException("30 FPS game check timed out: "+main.DiagnosticPreview?.FrameGenerationStatus); await Task.Delay(10); } }
        async Task<Dictionary<string,object>> Sample(int factor,int milliseconds=2000)
        {
            await Wait(()=>main.DiagnosticPreview?.FrameGenerationActive==true); await Task.Delay(300); var host=main.DiagnosticPreview!;
            var ticks = new List<long>();
            var sourceAges = new List<double>();
            long generated=0, shown=0; long inputs=source.Frames, unique=source.UniqueFrames;
            void Count(PresentedFrame info)
            {
                long now = Clock.Now;
                lock (ticks)
                {
                    ticks.Add(now);
                    if (info.Kind == PresentationKind.Source) sourceAges.Add(Math.Max(0, now - info.Time) / 10000.0);
                }
                Interlocked.Increment(ref shown);
                if (info.Kind == PresentationKind.Interpolated) Interlocked.Increment(ref generated);
            }
            host.FramePresented+=Count; var timer=Stopwatch.StartNew();
            try { await Task.Delay(milliseconds); } finally { host.FramePresented-=Count; }
            double seconds=timer.Elapsed.TotalSeconds; long[] timeArray; lock(ticks) timeArray=ticks.ToArray();
            double[] gaps=timeArray.Zip(timeArray.Skip(1),(left,right)=>(right-left)/10000.0).OrderBy(value=>value).ToArray();
            double fps=Interlocked.Read(ref shown)/seconds, generatedFps=Interlocked.Read(ref generated)/seconds;
            Require(fps > 30 * factor * .85 && fps < 30 * factor * 1.15,
                $"Game-content {factor}x target missed: {fps:F2} output, {(source.Frames-inputs)/seconds:F2} capture, {generatedFps:F2} generated. {host.FrameGenerationStatus}");
            Require(generatedFps>30*(factor-1)*.55,"The game mode mainly repeated captured images: "+generatedFps);
            double p95=gaps.Length==0 ? 1e6:gaps[Math.Min(gaps.Length-1,(int)(gaps.Length*.95))];
            Require(p95<1000.0/(30*factor)*1.8,"Game-content output retained an uneven presentation cadence: "+p95);
            double[] ages;
            lock (ticks) ages = sourceAges.OrderBy(value => value).ToArray();
            double Percentile(double[] values, double percentile) => values.Length == 0
                ? 0 : values[(int)((values.Length - 1) * percentile)];
            return new()
            {
                ["captureFps"]=(source.Frames-inputs)/seconds, ["distinctGameFps"]=(source.UniqueFrames-unique)/seconds,
                ["outputFps"]=fps, ["generatedFps"]=generatedFps, ["presentationGapP95Ms"]=p95,
                ["presentationGapP99Ms"]=Percentile(gaps, .99),
                ["sourceFrameAgeP50Ms"]=Percentile(ages, .50), ["sourceFrameAgeP95Ms"]=Percentile(ages, .95),
                ["sourceFramesMeasured"]=ages.Length, ["skippedCaptureDuplicates"]=host.SkippedCaptureDuplicates
            };
        }
        try
        {
            main.DiagnosticApplySettingsJson(JsonSerializer.Serialize(new { Folder=folder,Resolution=0,Bitrate=1,Sharpness=.25,Upscaling=1,Interpolation=1 }));
            Require(!main.DiagnosticCapped30Game,"Old preferences unexpectedly enabled game cadence.");
            main.DiagnosticSelectCapped30Game(true); string saved=main.DiagnosticSettingsJson;
            main.DiagnosticSelectCapped30Game(false); main.DiagnosticApplySettingsJson(saved);
            Require(main.DiagnosticCapped30Game && main.DiagnosticEmbedded.Capped30Game,"Game-cadence preference did not restore.");
            main.DiagnosticPrepareInput(); await main.DiagnosticConnectFullscreen();
            await Wait(()=>main.DiagnosticPreview?.FrameGenerationActive==true); var host=main.DiagnosticPreview!; double screen=DisplayTiming.RefreshRate(host.Handle); checks["screenRefreshHz"]=screen;
            host.DiagnosticRefreshRate=60;
            main.DiagnosticSelectCapped30Game(false); await Wait(()=>host.FrameGenerationActive && host.FrameGenerationStatus.StartsWith("2×"));
            checks["automatic30Inside60On60Hz"]=await Sample(2,2500);
            main.DiagnosticSelectCapped30Game(true); await Wait(()=>host.FrameGenerationActive && host.FrameGenerationStatus.StartsWith("30 FPS game"));
            checks["game2x60Hz"]=await Sample(2,4000); Require(source.Frames-source.UniqueFrames>10&&source.Frames>source.UniqueFrames*1.7,"The 60 FPS source did not exercise repeated captures.");
            host.DiagnosticRefreshRate=0;main.DiagnosticSelectCapped30Game(false);
            for(int factor=3;factor<=4;factor++)
            {
                if(!FrameGenerationPolicy.CanGenerate(333332,screen,640,360,factor)) { checks["game"+factor+"xLive"]="not exercised: screen refresh below target"; continue; }
                main.DiagnosticSelectFrameGeneration((FrameGenerationMode)(factor-1));
                await Wait(()=>host.FrameGenerationActive && host.FrameGenerationStatus.Contains(factor+"×")); checks["game"+factor+"xLive"]=await Sample(factor);
            }
            main.DiagnosticSelectFrameGeneration(FrameGenerationMode.Adaptive2x); await Wait(()=>host.FrameGenerationActive && host.FrameGenerationStatus.Contains("2×"));
            source.HoldMotion=true; await Task.Delay(500); long before=host.GeneratedPresentations; long presented=main.DiagnosticPresented; await Task.Delay(700);
            Require(host.GeneratedPresentations==before && main.DiagnosticPresented-presented>32,"Static game content invented motion or stopped its paced output."); source.HoldMotion=false;
            await Sample(2,1200); checks["staticGamePacingAndMotionResume"]="pass";
            source.PauseFrames=true; await Wait(()=>!host.FrameGenerationActive); long idle=main.DiagnosticPresented; await Task.Delay(180); Require(main.DiagnosticPresented-idle<=1,"Missing input continued a GPU presentation loop.");
            source.PauseFrames=false; await Wait(()=>host.FrameGenerationActive); await Sample(2,1200); checks["missingInputIdleAndResume"]="pass";
            var phaseRates=new List<double>();
            for(int cycle=0;cycle<cycles;cycle++)
            {
                File.WriteAllText(Path.Combine(folder,"stage.txt"),"game fullscreen round trip "+(cycle+1));
                await main.DiagnosticWindowed(); var windowed=await Sample(2,500); phaseRates.Add((double)windowed["outputFps"]);
                await main.DiagnosticFullscreen(); main.DiagnosticMaximizeFullscreen(); var full=await Sample(2,500); phaseRates.Add((double)full["outputFps"]);
                if(cycle%2==1) { main.DiagnosticSelectCapped30Game(false); await Task.Delay(100); main.DiagnosticSelectCapped30Game(true); await Wait(()=>main.DiagnosticPreview?.FrameGenerationActive==true); }
            }
            main.DiagnosticSelectUpscaler(PreviewUpscaler.RtxVideo); await Wait(()=>main.DiagnosticPreview?.RtxVideoRequested==true || main.DiagnosticPreview?.UpscalingFallbackReason!=null); await Sample(2,1500); checks["gameCadenceWithDirectRtxVideoSdk"]="pass"; main.DiagnosticSelectUpscaler(PreviewUpscaler.Enhanced);
            await main.DiagnosticRecord(folder); var recording=main.DiagnosticRecorder ?? throw new InvalidOperationException("Game-mode recording did not start."); await Task.Delay(1100); await main.DiagnosticRecord(folder);
            Require(recording.VideoSamplesWritten>=48 && recording.VideoSamplesWritten<=78,"Game cadence changed the captured recording sample rate."); checks["sourceCaptureRecordingSamples"]=recording.VideoSamplesWritten;
            await main.DiagnosticWindowed(); await Wait(()=>main.DiagnosticPreview?.FrameGenerationActive==true); await Task.Delay(200); main.UpdateLayout();
            var image=new RenderTargetBitmap((int)Math.Ceiling(main.ActualWidth),(int)Math.Ceiling(main.ActualHeight),96,96,PixelFormats.Pbgra32); image.Render(main); var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using(var file=File.Create(Path.Combine(folder,"interface.png"))) png.Save(file);
            main.DiagnosticStaleFailure(main.DiagnosticSession); await Wait(()=>!main.DiagnosticConnected && !main.DiagnosticBusy); await main.DiagnosticDisconnect();
            checks["gameFullscreenRoundTrips"]=cycles; checks["gameFullscreenPhaseFps"]=phaseRates; checks["gamePreferencesToggleFullscreenRecordingDisconnect"]="pass";
        }
        finally { source.PauseFrames=false; await main.DiagnosticClose().WaitAsync(TimeSpan.FromSeconds(5)); }
        return checks;
    }
}