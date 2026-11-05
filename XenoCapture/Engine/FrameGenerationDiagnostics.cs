using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using XenoCapture.UI;

namespace XenoCapture.Engine;
internal static class FrameGenerationDiagnostics
{
    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    public static async Task Run(string folder, bool soak = false)
    {
        Directory.CreateDirectory(folder);
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--gap-recovery-only") >= 0)
        {
            var recovery = await RunGapRecoveryUi();
            recovery["result"] = "PASS";
            await File.WriteAllTextAsync(Path.Combine(folder, "results.json"), JsonSerializer.Serialize(recovery));
            return;
        }
        if(Array.IndexOf(Environment.GetCommandLineArgs(),"--high-rate-only")>=0)
        {
            var rate=await RunHighRateUi(folder);rate["result"]="PASS";
            await File.WriteAllTextAsync(Path.Combine(folder,"results.json"),JsonSerializer.Serialize(rate,new JsonSerializerOptions{WriteIndented=true}));return;
        }
        var results = await Task.Run(() => RunGpu(folder));
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--gpu-only") < 0)
        {
            foreach (var item in await RunUi(folder, soak ? 20 : 0)) results[item.Key] = item.Value;
            foreach (var item in await RunGapRecoveryUi()) results[item.Key] = item.Value;
            foreach (var item in await RunHighRateUi(folder)) results[item.Key] = item.Value;
        }
        results["result"] = "PASS";
        await File.WriteAllTextAsync(Path.Combine(folder, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
    static void CheckHighRateCadence()
    {
        const long period = 166_667, start = 1_000_000;
        var stable = new HighRateCadence();
        var limited = new HighRateCadence();
        for (int i = 0; i < 270; i++)
            stable.Observe(start + i * 41_667L, period);
        for (int i = 0; i < 100; i++)
            limited.Observe(start + i * 55_556L, period);
        Require(!stable.PreferThreePhases && !limited.PreferThreePhases, "A sustainable rate or a short stall triggered a downgrade.");
        for (int i = 100; i < 205; i++)
            limited.Observe(start + i * 55_556L, period);
        Require(limited.PreferThreePhases && !HighRateCadence.Eligible(333_333), "Sustained missed high-refresh slots did not select the steadier cadence.");
    }

    internal static Dictionary<string, object> RunGpu(string folder)
    {
        Directory.CreateDirectory(folder); using var apartment = new Apartment();
        var checks = new Dictionary<string, object>();
        CheckHighRateCadence();
        checks["adaptiveHighRateCadence"] = "pass";
        CheckPrediction(checks);
        using var first = Pattern(0); using var second = Pattern(16); using var expected = Pattern(8);
        first.Time = Clock.Now; second.Time = first.Time + 333333; expected.Time = first.Time + 166667;
        using (var renderer = new D3DPreview(0, 640, 360) { DisableFrameBudgetForDiagnostics = true })
        {
            Require(renderer.PrepareInterpolation(first), renderer.FrameGenerationFailure ?? "First-frame preparation failed.");
            Require(renderer.PrepareInterpolation(second), renderer.FrameGenerationFailure ?? "Second-frame preparation failed.");
            checks["cameraMotionConfidence"] = renderer.FrameMotionConfidence; checks["analysisMs"] = renderer.FrameAnalysisMs;
            checks["hardwareOpticalFlowActive"]=renderer.HardwareOpticalFlowActive;checks["opticalFlowFallback"]=renderer.OpticalFlowFallbackReason??"none";
            File.WriteAllText(Path.Combine(folder,"gpu-stage.json"),JsonSerializer.Serialize(checks));
            Require(renderer.CanGenerateMidpoint, "Known camera translation was rejected: " + renderer.FrameGenerationDetail);
            var info = renderer.RenderMidpoint(0, PreviewUpscaler.Standard); byte[] middle = renderer.Readback();
            Require(info.Kind == PresentationKind.Interpolated, "No motion-interpolated frame was rendered.");
            double generatedError = Error(middle, expected.Bytes), repeatError = Error(first.Bytes, expected.Bytes);
            checks["midpointMeanAbsoluteError"] = generatedError; checks["repeatedFrameMeanAbsoluteError"] = repeatError;
            checks["translationUnmatchedHighContrastFraction"] = renderer.UnmatchedHighContrastFraction;
            Require(generatedError < repeatError * .65, "The generated frame did not approach the known midpoint image.");
            UpscalingDiagnostics.SavePng(Path.Combine(folder, "previous.png"), first.Bytes, 640, 360);
            UpscalingDiagnostics.SavePng(Path.Combine(folder, "generated.png"), middle, 640, 360);
            UpscalingDiagnostics.SavePng(Path.Combine(folder, "expected.png"), expected.Bytes, 640, 360);
            int hudDifferences = 0;
            for (int y = 30; y < 68; y++) for (int x = 40; x < 200; x++)
            for (int channel = 0; channel < 3; channel++) if (middle[(y * 640 + x) * 4 + channel] != expected.Bytes[(y * 640 + x) * 4 + channel]) hudDifferences++;
            Require(hudDifferences == 0, "Static HUD pixels moved or ghosted."); checks["staticHudDifferences"] = hudDifferences;
            renderer.RenderCachedFrame(0, PreviewUpscaler.Standard); Require(second.Bytes.AsSpan(0, second.Length).SequenceEqual(renderer.Readback()), "Source frames changed during frame-generation presentation.");
            double analysisTotal = 0; int confidentPairs = 0; long benchmarkTime = second.Time;
            for (int pass = 0; pass < 16; pass++)
            {
                var input = pass % 2 == 0 ? first : second; benchmarkTime += 333333; input.Time = benchmarkTime;
                renderer.PrepareInterpolation(input); if (pass >= 4) analysisTotal += renderer.FrameAnalysisMs;
                if (renderer.CanGenerateMidpoint) confidentPairs++;
            }
            checks["warmAnalysisMeanMs"] = analysisTotal / 12; checks["confidentPairsIn16MotionSamples"] = confidentPairs;
            second.Time = first.Time + 40 * 333333L; renderer.PrepareInterpolation(second);
            second.Time += 333333; renderer.PrepareInterpolation(second); renderer.RenderMidpoint(0, PreviewUpscaler.Standard);
            Require(!renderer.CanGenerateMidpoint && second.Bytes.AsSpan(0, second.Length).SequenceEqual(renderer.Readback()), "Static input produced invented pixels.");
            using var cut = new Packet(640 * 360 * 4, false, 640, 360) { Time = second.Time + 333333, Duration = 333333 };
            for (int i = 0; i < cut.Length; i += 4) { cut.Bytes[i] = 240; cut.Bytes[i + 1] = 10; cut.Bytes[i + 2] = 10; cut.Bytes[i + 3] = 255; }
            renderer.PrepareInterpolation(cut); var fallback = renderer.RenderMidpoint(0, PreviewUpscaler.Standard);
            Require(!renderer.CanGenerateMidpoint && fallback.Kind == PresentationKind.Repeated && second.Bytes.AsSpan(0, second.Length).SequenceEqual(renderer.Readback()), "A scene cut blended unrelated images.");
            checks["sceneCutFallback"] = "pass"; checks["surfaceBytesAt640x360"] = renderer.FrameGenerationSurfaceBytes;
            renderer.ResetInterpolation(); Require(renderer.FrameGenerationSurfaceBytes == 0, "Disabling frame generation retained its frame history.");
        }
        using (var renderer = new D3DPreview(0, 1920, 1080) { DisableFrameBudgetForDiagnostics = true })
        using (var a = Pattern(0,1920,1080)) using (var b = Pattern(12,1920,1080))
        {
            long time = Clock.Now; double total = 0; int active = 0;
            for (int pass = 0; pass < 14; pass++)
            {
                var frame = pass % 2 == 0 ? a : b; frame.Time = time; frame.Duration = 166667; time += 166667;
                Require(renderer.PrepareInterpolation(frame), renderer.FrameGenerationFailure ?? "1080p interpolation preparation failed.");
                if (pass >= 4) { total += renderer.FrameAnalysisMs; if (renderer.CanGenerateMidpoint) active++; }
                renderer.RenderMidpoint(.25, PreviewUpscaler.Standard);
            }
            renderer.Readback(); checks["warm1080pAnalysisMeanMs"] = total / 10; checks["confident1080pPairs"] = active;
            checks["surfaceBytesAt1080p"] = renderer.FrameGenerationSurfaceBytes;
            Require(active > 5, "1080p motion interpolation was not effective.");
            Require(renderer.FrameGenerationSurfaceBytes < 32 * 1024 * 1024, "The 32 MiB GPU surface budget was exceeded.");
        }
        using (var unsupported = new D3DPreview(0,640,360) { ForceFrameGenerationUnavailable = true })
        {
            Require(!unsupported.PrepareInterpolation(first) && unsupported.FrameGenerationFailure != null && unsupported.FrameGenerationSurfaceBytes == 0, "Unsupported frame generation did not fall back cleanly.");
            unsupported.Draw(first,0); Require(first.Bytes.AsSpan(0,first.Length).SequenceEqual(unsupported.Readback()), "Unsupported frame generation damaged native preview.");
        }
        Require(FrameGenerationPolicy.CanGenerate(333333, 59, 1920,1080, 2), "Fractional refresh-rate rounding was not tolerated.");
        Require(FrameGenerationPolicy.CanGenerate(333333, 60, 1920,1080, 2), "30-to-60 policy rejected a supported screen.");
        for (int multiplier = 2; multiplier <= 4; multiplier++)
        {
            Require(FrameGenerationPolicy.Multiplier((FrameGenerationMode)(multiplier - 1)) == multiplier, "A saved mode maps to the wrong multiplier.");
            Require(FrameGenerationPolicy.CanGenerate(166667, 60 * multiplier, 1920,1080, multiplier), "60 FPS refresh policy rejected factor " + multiplier);
            Require(!FrameGenerationPolicy.CanGenerate(166667, 60, 1920,1080, multiplier), "A high-rate mode was enabled on a 60 Hz screen.");
            Require(!FrameGenerationPolicy.CanGenerate(166667, 240, 3840,2160, multiplier), "The lightweight source-resolution budget was ignored.");
            long previous = 10_000;
            for (int step = 1; step <= multiplier; step++)
            {
                long due = FrameGenerationPolicy.PhaseTime(10_000, 333333, step, multiplier);
                Require(due > previous && due == 10_000 + 333333L * step / multiplier, "Fractional deadlines drifted or repeated.");
                Require(FrameGenerationPolicy.MissedPhases(due + 333333L * 3 / (4 * multiplier), due, 333333, multiplier) == 0,
                    "DXGI readiness within the current interval discarded a valid phase.");
                long interval = 333333 / multiplier;
                Require(FrameGenerationPolicy.MissedPhases(due + interval, due, 333333, multiplier) == 1
                    && FrameGenerationPolicy.MissedPhases(due + 333333, due, 333333, multiplier) == multiplier,
                    "A missed phase or a full source interval was classified incorrectly.");
                previous = due;
            }
            Require(previous == 343333, "The fractional schedule changed the source frame boundary.");
        }
        Require(!FrameGenerationPolicy.CanGenerate(166667,240,1920,1080,5) && FrameGenerationPolicy.Multiplier((FrameGenerationMode)99) == 1, "Invalid factors were accepted.");
        Require(FrameGenerationPolicy.ShouldPresentPhase(PresentationKind.Interpolated, true, 10, 0)
            && !FrameGenerationPolicy.ShouldPresentPhase(PresentationKind.Repeated, true, 10, 10)
            && FrameGenerationPolicy.ShouldPresentPhase(PresentationKind.Source, false, 10, 10)
            && FrameGenerationPolicy.ShouldPresentPhase(PresentationKind.Repeated, true, 11, 10),
            "A rejected late phase could rewind the visible frame or suppress a new pair.");
        checks["latePhaseDoesNotRewind"] = "pass";
        using (var renderer = new D3DPreview(0,640,360) { DisableFrameBudgetForDiagnostics = true })
        using (var a = Pattern(0)) using (var b = Pattern(12))
        {
            a.Time = 1_000_000; b.Time = a.Time + 333333;
            Require(renderer.PrepareInterpolation(a) && renderer.PrepareInterpolation(b) && renderer.CanGenerateMidpoint, "Fractional-frame motion was rejected.");
            long bytes = renderer.FrameGenerationSurfaceBytes;
            for (int multiplier = 3; multiplier <= 4; multiplier++)
            {
                byte[]? previousPixels = null; var errors = new List<double>();
                for (int step = 1; step < multiplier; step++)
                {
                    using var expectedStep = Pattern(12 * step / multiplier);
                    var info = renderer.RenderIntermediate(step,multiplier,0,PreviewUpscaler.Standard); var pixels = renderer.Readback();
                    double error = Error(pixels,expectedStep.Bytes); errors.Add(error);
                    Require(info.Kind == PresentationKind.Interpolated && info.Time == a.Time + 333333L * step / multiplier && info.Duration == 333333 / multiplier, "Generated-frame metadata has the wrong fractional timing.");
                    Require(error < Error(a.Bytes,expectedStep.Bytes) * .65, "An intermediate frame did not approach its known image.");
                    Require(previousPixels == null || !pixels.AsSpan().SequenceEqual(previousPixels), "Different interpolation fractions produced identical images.");
                    for (int y = 30; y < 68; y++) for (int x = 40; x < 200; x++)
                    for (int channel = 0; channel < 3; channel++) Require(pixels[(y*640+x)*4+channel] == expectedStep.Bytes[(y*640+x)*4+channel], "A fractional frame altered the static HUD.");
                    UpscalingDiagnostics.SavePng(Path.Combine(folder,$"generated-{multiplier}x-{step}.png"),pixels,640,360); previousPixels = pixels;
                }
                checks[$"fractional{multiplier}xMeanAbsoluteErrors"] = errors;
                Require(renderer.FrameGenerationSurfaceBytes == bytes, "A higher factor allocated additional history or output surfaces.");
            }
            renderer.RenderMidpoint(0,PreviewUpscaler.Standard); var oldMidpoint = renderer.Readback();
            renderer.RenderIntermediate(1,2,0,PreviewUpscaler.Standard); Require(oldMidpoint.AsSpan().SequenceEqual(renderer.Readback()), "The 2x midpoint changed.");
            using var cut = new Packet(640*360*4,false,640,360) { Time = b.Time+333333, Duration = 333333 };
            Array.Fill(cut.Bytes,(byte)255,0,cut.Length); renderer.PrepareInterpolation(cut);
            for (int multiplier = 3; multiplier <= 4; multiplier++) for (int step = 1; step < multiplier; step++)
            {
                var fallback = renderer.RenderIntermediate(step,multiplier,0,PreviewUpscaler.Standard);
                Require(fallback.Kind == PresentationKind.Repeated && fallback.Time == b.Time && b.Bytes.AsSpan(0,b.Length).SequenceEqual(renderer.Readback()), "A scene cut generated pixels at a higher factor.");
            }
            checks["fractionalTimingHudCutAndSurfaceReuse"] = "pass";
        }
        checks["refreshRateAndMemoryLimits"] = "pass"; checks["staticSourceIdentity"] = "pass"; checks["sourceFrameIdentity"] = "pass";
        foreach(var item in HardwareInterpolationDiagnostics.Run(folder))checks[item.Key]=item.Value;
        checks["captureHardware"] = "not exercised"; checks["measuresButtonToScreenLatency"] = false; return checks;
    }
    internal static async Task<Dictionary<string, object>> RunUi(string folder, int cycles = 0)
    {
        var checks = new Dictionary<string, object>();
        async Task Wait(Func<bool> condition)
        { var clock = Stopwatch.StartNew(); while (!condition()) { if (clock.ElapsedMilliseconds > 8000) throw new TimeoutException("Frame-generation UI check timed out."); await Task.Delay(10); } }
        var source = new FrameGenerationTestSource();
        var main = new MainWindow(false, false, (_, _) => new CaptureStream(() => source, TimeSpan.FromSeconds(2))) { ShowActivated = false };
        main.Show();
        try
        {
            main.DiagnosticApplySettingsJson(JsonSerializer.Serialize(new { Folder = folder, Resolution = 0, Bitrate = 1, Sharpness = .25, Upscaling = 1 }));
            Require(main.DiagnosticFrameGeneration == FrameGenerationMode.Off, "Old settings unexpectedly enabled buffering.");
            main.DiagnosticSelectFrameGeneration(FrameGenerationMode.Adaptive2x); string saved = main.DiagnosticSettingsJson;
            main.DiagnosticSelectFrameGeneration(FrameGenerationMode.Off); main.DiagnosticApplySettingsJson(saved);
            Require(main.DiagnosticFrameGeneration == FrameGenerationMode.Adaptive2x, "The frame-generation preference did not restore.");
            main.DiagnosticPrepareInput(); await main.DiagnosticConnectFullscreen();
            await Wait(() => main.DiagnosticPreview?.FrameGenerationActive == true);
            var host = main.DiagnosticPreview!; long shown = 0, generated = 0; double latency = 0;
            void OnPresent(PresentedFrame frame) { Interlocked.Increment(ref shown); if (frame.Kind == PresentationKind.Interpolated) Interlocked.Increment(ref generated); latency = Math.Max(latency,(Clock.Now-frame.Time)/10000.0); }
            host.FramePresented += OnPresent;
            try
            {
                await Task.Delay(300); Interlocked.Exchange(ref shown,0); Interlocked.Exchange(ref generated,0);
                long input = source.Frames; var clock = Stopwatch.StartNew(); await Task.Delay(4000);
                double elapsed = clock.Elapsed.TotalSeconds;
                checks["liveInputFps"] = (source.Frames-input)/elapsed; checks["liveOutputFps"] = Interlocked.Read(ref shown)/elapsed;
                checks["motionInterpolatedPresentations"] = Interlocked.Read(ref generated); checks["maximumSyntheticSampleAgeMs"] = latency;
                checks["frameGenerationStatus"] = host.FrameGenerationStatus;
                Require((double)checks["liveOutputFps"] > 50 && (double)checks["liveInputFps"] > 26, "The 30-to-60 presentation path stalled.");
                Require(generated > 10, "The live test only repeated frames and did not interpolate motion.");
            }
            finally { host.FramePresented -= OnPresent; }

            main.DiagnosticSelectFrameGeneration(FrameGenerationMode.Predictive60);
            await Wait(() => host.FrameGenerationActive && host.FrameGenerationStatus.Contains("no frame buffering", StringComparison.Ordinal));
            long sources = 0, predictions = 0;
            void CountPrediction(PresentedFrame frame)
            {
                if (frame.Kind == PresentationKind.Source) Interlocked.Increment(ref sources);
                if (frame.Kind == PresentationKind.Predicted) Interlocked.Increment(ref predictions);
            }
            host.FramePresented += CountPrediction;
            var predictiveSample = Stopwatch.StartNew();
            try { await Task.Delay(2000); }
            finally { host.FramePresented -= CountPrediction; }
            double sourceFps = Interlocked.Read(ref sources) / predictiveSample.Elapsed.TotalSeconds;
            double predictionFps = Interlocked.Read(ref predictions) / predictiveSample.Elapsed.TotalSeconds;
            checks["predictiveSourceFps"] = sourceFps;
            checks["predictiveGeneratedFps"] = predictionFps;
            Require(sourceFps > 26 && predictionFps > 5, "Prediction did not fill spare slots while keeping the source cadence.");

            main.DiagnosticSelectFrameGeneration(FrameGenerationMode.Off);
            await Wait(() => main.DiagnosticPreview?.FrameGenerationActive == false);
            Require(main.DiagnosticEmbedded.FrameGeneration == FrameGenerationMode.Off, "Off did not reach the windowed host.");
            main.DiagnosticSelectFrameGeneration(FrameGenerationMode.Adaptive2x);
            await Wait(() => main.DiagnosticPreview?.FrameGenerationActive == true);
            await main.DiagnosticWindowed(); await Wait(() => main.DiagnosticPreview?.FrameGenerationActive == true);
            await main.DiagnosticFullscreen(); await Wait(() => main.DiagnosticPreview?.FrameGenerationActive == true);
            var buckets = new List<double>(); long soakGenerated = 0;
            async Task Sample(string phase)
            {
                File.WriteAllText(Path.Combine(folder,"stage.txt"),phase);
                await Wait(() => main.DiagnosticPreview?.FrameGenerationActive == true); await Task.Delay(250);
                var current = main.DiagnosticPreview!; long count = 0;
                void OnFrame(PresentedFrame frame) { Interlocked.Increment(ref count); if (frame.Kind == PresentationKind.Interpolated) Interlocked.Increment(ref soakGenerated); }
                current.FramePresented += OnFrame;
                try { var clock = Stopwatch.StartNew(); await Task.Delay(1000); double fps = Interlocked.Read(ref count)/clock.Elapsed.TotalSeconds; buckets.Add(fps); Require(fps>48,"Generated output stalled during "+phase+": "+fps); }
                finally { current.FramePresented -= OnFrame; }
            }
            for (int cycle=0; cycle<cycles; cycle++)
            {
                await main.DiagnosticWindowed(); await Sample("windowed "+(cycle+1));
                await main.DiagnosticFullscreen(); main.DiagnosticMaximizeFullscreen(); await Sample("fullscreen "+(cycle+1));
                if (cycle%5==4) { main.DiagnosticSelectFrameGeneration(FrameGenerationMode.Off); await Wait(()=>main.DiagnosticPreview?.FrameGenerationActive==false); main.DiagnosticSelectFrameGeneration(FrameGenerationMode.Adaptive2x); await Wait(()=>main.DiagnosticPreview?.FrameGenerationActive==true); }
            }
            if (cycles>0)
            {
                checks["soakFullscreenRoundTrips"]=cycles; checks["soakMinimumOutputFps"]=System.Linq.Enumerable.Min(buckets); checks["soakAverageOutputFps"]=System.Linq.Enumerable.Average(buckets); checks["soakMotionInterpolatedFrames"]=soakGenerated;
                Require(soakGenerated>cycles*20,"Soak did not exercise enough motion-interpolated presentations.");
            }
            await main.DiagnosticRecord(folder); var recording = main.DiagnosticRecorder ?? throw new InvalidOperationException("Recording did not start during frame generation.");
            await Task.Delay(1100); await main.DiagnosticRecord(folder);
            Require(recording.VideoSamplesWritten>=20 && recording.VideoSamplesWritten<=45,"Frame generation changed the recording's source frame rate.");
            checks["sourceRateRecordingWhileGenerating"] = recording.VideoSamplesWritten;
            await main.DiagnosticWindowed(); await Task.Delay(100); main.UpdateLayout();
            var image = new RenderTargetBitmap((int)Math.Ceiling(main.ActualWidth), (int)Math.Ceiling(main.ActualHeight), 96,96,PixelFormats.Pbgra32); image.Render(main);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using (var file = File.Create(Path.Combine(folder,"interface.png"))) png.Save(file);
            main.DiagnosticStaleFailure(main.DiagnosticSession); await Wait(()=>!main.DiagnosticConnected && !main.DiagnosticBusy);
            await main.DiagnosticDisconnect(); Require(!main.DiagnosticConnected && !main.DiagnosticBusy, "Disconnect failed with generated output pending.");
            checks["settingsAndLiveSwitching"] = "pass"; checks["fullscreenAndDisconnectWhileGenerating"] = "pass";
        }
        finally { await main.DiagnosticClose().WaitAsync(TimeSpan.FromSeconds(5)); }
        for (int multiplier = 3; multiplier <= 4; multiplier++)
            foreach (var item in await RunMultiplierUi(folder,multiplier,cycles > 0 ? 6 : 2)) checks[item.Key] = item.Value;
        return checks;
    }
    static async Task<Dictionary<string, object>> RunGapRecoveryUi()
    {
        var source = new FrameGenerationTestSource(60, dropEvery: 8);
        var main = new MainWindow(false, false, (_, _) => new CaptureStream(() => source, TimeSpan.FromSeconds(2)))
            { ShowActivated = false };
        var checks = new Dictionary<string, object>();
        main.Show();
        try
        {
            main.DiagnosticPrepareInput();
            await main.DiagnosticConnectFullscreen(fastestMonitor: true);
            var host = main.DiagnosticPreview!;
            double refresh = DisplayTiming.RefreshRate(host.Handle);
            checks["gapRecoveryRefreshHz"] = refresh;
            if (!SourceFramePacing.HasRefreshHeadroom(refresh, 166_666))
            {
                checks["gapRecovery"] = "not exercised: display refresh below requirement";
                return checks;
            }
            async Task<(double LongGapsPerSecond, double SourceFps, double PredictedFps)> Sample()
            {
                await Task.Delay(400);
                long previous = 0;
                int longGaps = 0, sources = 0, predictions = 0;
                var gate = new object();
                void Count(PresentedFrame frame)
                {
                    if (frame.Kind is not (PresentationKind.Source or PresentationKind.Predicted)) return;
                    lock (gate)
                    {
                        long now = Clock.Now;
                        if (previous != 0 && now - previous > 270_000) longGaps++;
                        previous = now;
                        if (frame.Kind == PresentationKind.Source) sources++;
                        else predictions++;
                    }
                }
                host.FramePresented += Count;
                var watch = Stopwatch.StartNew();
                try { await Task.Delay(3000); }
                finally { host.FramePresented -= Count; }
                double seconds = watch.Elapsed.TotalSeconds;
                lock (gate) return (longGaps / seconds, sources / seconds, predictions / seconds);
            }

            var baseline = await Sample();
            main.DiagnosticSelectFrameGeneration(FrameGenerationMode.RecoverGaps);
            string saved = main.DiagnosticSettingsJson;
            main.DiagnosticSelectFrameGeneration(FrameGenerationMode.Off);
            main.DiagnosticApplySettingsJson(saved);
            Require(host.FrameGeneration == FrameGenerationMode.RecoverGaps, "The gap recovery setting did not restore.");
            var recovered = await Sample();
            Require(baseline.PredictedFps == 0 && recovered.PredictedFps > 2 && recovered.PredictedFps < 10,
                "Gap recovery did not selectively fill missing frames.");
            Require(recovered.SourceFps > 48 && recovered.LongGapsPerSecond < baseline.LongGapsPerSecond * .85,
                $"Gap recovery did not reduce long gaps while preserving source delivery ({baseline.LongGapsPerSecond:F1} to {recovered.LongGapsPerSecond:F1}/s).");
            checks["gapRecovery"] = new
            {
                baselineLongGapsPerSecond = baseline.LongGapsPerSecond, recoveryLongGapsPerSecond = recovered.LongGapsPerSecond,
                baselineSourceFps = baseline.SourceFps, recoverySourceFps = recovered.SourceFps, predictedFps = recovered.PredictedFps
            };
            return checks;
        }
        finally { await main.DiagnosticClose().WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    static async Task<Dictionary<string,object>> RunMultiplierUi(string folder, int multiplier, int cycles)
    {
        var checks = new Dictionary<string,object>(); var source = new FrameGenerationTestSource(60 / multiplier);
        var main = new MainWindow(false,false,(_,_) => new CaptureStream(() => source,TimeSpan.FromSeconds(2))) { ShowActivated = false };
        var mode = (FrameGenerationMode)(multiplier-1); main.Show();
        async Task Wait(Func<bool> condition)
        { var clock = Stopwatch.StartNew(); while (!condition()) { if (clock.ElapsedMilliseconds>8000) throw new TimeoutException(multiplier+"x UI check timed out: "+main.DiagnosticPreview?.FrameGenerationStatus); await Task.Delay(10); } }
        try
        {
            main.DiagnosticApplySettingsJson(JsonSerializer.Serialize(new { Folder = folder, Resolution = 0, Bitrate = 1, Sharpness = .25, Upscaling = 1, Interpolation = 99 }));
            Require(main.DiagnosticFrameGeneration == FrameGenerationMode.Off,"Unknown saved mode did not default to Off.");
            main.DiagnosticSelectFrameGeneration(mode); string saved = main.DiagnosticSettingsJson;
            main.DiagnosticSelectFrameGeneration(FrameGenerationMode.Off); main.DiagnosticApplySettingsJson(saved);
            Require(main.DiagnosticFrameGeneration == mode,"A higher-factor preference did not restore.");
            main.DiagnosticPrepareInput(); await main.DiagnosticConnectFullscreen();
            await Wait(()=>main.DiagnosticPreview?.FrameGenerationActive==true);
            var host = main.DiagnosticPreview!; long shown = 0, generated = 0;
            void Count(PresentedFrame frame) { Interlocked.Increment(ref shown); if (frame.Kind==PresentationKind.Interpolated) Interlocked.Increment(ref generated); }
            await Task.Delay(500); host.FramePresented += Count;
            try
            {
                long input = source.Frames; var clock = Stopwatch.StartNew(); await Task.Delay(3000); double seconds = clock.Elapsed.TotalSeconds;
                double outputFps = Interlocked.Read(ref shown)/seconds; double inputFps = (source.Frames-input)/seconds;
                checks[$"live{multiplier}xInputFps"] = inputFps; checks[$"live{multiplier}xOutputFps"] = outputFps; checks[$"live{multiplier}xGeneratedFrames"] = Interlocked.Read(ref generated);
                Require(outputFps>50 && outputFps/inputFps>multiplier*.85,"The higher-factor preview did not multiply its presentation rate.");
                Require(Interlocked.Read(ref generated)>30*(multiplier-1),"The higher-factor preview only repeated real frames.");
                Require(host.FrameGenerationStatus.StartsWith(multiplier+"×"),"The active factor was not displayed.");
            }
            finally { host.FramePresented -= Count; }
            for (int cycle=0; cycle<cycles; cycle++)
            {
                await main.DiagnosticWindowed(); await Wait(()=>main.DiagnosticPreview?.FrameGenerationActive==true); await Task.Delay(100);
                await main.DiagnosticFullscreen(); main.DiagnosticMaximizeFullscreen(); await Wait(()=>main.DiagnosticPreview?.FrameGenerationActive==true); await Task.Delay(100);
                main.DiagnosticSelectFrameGeneration(FrameGenerationMode.Off); await Wait(()=>main.DiagnosticPreview?.FrameGenerationActive==false);
                main.DiagnosticSelectFrameGeneration(mode); await Wait(()=>main.DiagnosticPreview?.FrameGenerationActive==true);
            }
            host = main.DiagnosticPreview!;
            if (multiplier==3)
            {
                // Conservative synthetic refresh cap exercises rejection on any physical screen.
                host.DiagnosticRefreshRate = 60; main.DiagnosticSelectFrameGeneration(FrameGenerationMode.Adaptive4x);
                await Wait(()=>!host.FrameGenerationActive && host.FrameGenerationStatus.StartsWith("Needs 80 Hz"));
                host.DiagnosticRefreshRate = 0; main.DiagnosticSelectFrameGeneration(mode); await Wait(()=>host.FrameGenerationActive);
                checks["higherFactorRefreshFallback"] = "pass";
            }
            await main.DiagnosticRecord(folder); var recording = main.DiagnosticRecorder ?? throw new InvalidOperationException("Higher-factor recording did not start.");
            await Task.Delay(1100); await main.DiagnosticRecord(folder);
            Require(recording.VideoSamplesWritten>=60/multiplier-4 && recording.VideoSamplesWritten<=60/multiplier+7,"A higher multiplier changed the recording's source rate.");
            checks[$"sourceRateRecording{multiplier}x"] = recording.VideoSamplesWritten;
            // Change factors while frames are arriving, then stop with fractional deadlines pending.
            main.DiagnosticSelectFrameGeneration(FrameGenerationMode.Adaptive2x); await Wait(()=>host.FrameGenerationActive && host.FrameGenerationStatus.StartsWith("2×"));
            main.DiagnosticSelectFrameGeneration(mode); await Wait(()=>host.FrameGenerationActive && host.FrameGenerationStatus.StartsWith(multiplier+"×"));
            main.DiagnosticStaleFailure(main.DiagnosticSession); await Wait(()=>!main.DiagnosticConnected && !main.DiagnosticBusy); await main.DiagnosticDisconnect();
            checks[$"settingsSwitchFullscreenDisconnect{multiplier}x"] = "pass"; checks[$"fullscreenRoundTrips{multiplier}x"] = cycles;
        }
        finally { await main.DiagnosticClose().WaitAsync(TimeSpan.FromSeconds(5)); }
        return checks;
    }
    internal static async Task<Dictionary<string,object>> RunHighRateUi(string folder)
    {
        var checks=new Dictionary<string,object>();var source=new FrameGenerationTestSource(60);
        var main=new MainWindow(false,false,(_,_)=>new CaptureStream(()=>source,TimeSpan.FromSeconds(2))){ShowActivated=false};main.Show();
        async Task Wait(Func<bool> condition){var t=Stopwatch.StartNew();while(!condition()){if(t.ElapsedMilliseconds>10000)throw new TimeoutException("High-rate check: "+main.DiagnosticPreview?.FrameGenerationStatus);await Task.Delay(10);}}
        try
        {
            main.DiagnosticApplySettingsJson(JsonSerializer.Serialize(new{Folder=folder,Resolution=0,Bitrate=1,Sharpness=.25,Upscaling=1,Interpolation=1}));
            main.DiagnosticPrepareInput();await main.DiagnosticConnectFullscreen(fastestMonitor: true);
            await Wait(()=>main.DiagnosticPreview?.FrameGenerationActive==true);await Task.Delay(350);
            var host=main.DiagnosticPreview!;double refresh=DisplayTiming.RefreshRate(host.Handle);checks["highRateScreenRefreshHz"]=refresh;
            for(int multiplier=2;multiplier<=4;multiplier++)
            {
                if(!FrameGenerationPolicy.CanGenerate(166667,refresh,640,360,multiplier)){checks[$"highRate60x{multiplier}"]="not exercised: display refresh below target";continue;}
                main.DiagnosticSelectFrameGeneration((FrameGenerationMode)(multiplier-1));await Wait(()=>host.FrameGenerationActive);await Task.Delay(multiplier == 4 ? 1600 : 700);
                long shown=0,generated=0,input=source.Frames;var submissions=new List<double>();var presentationTimes=new List<long>();
                void Count(PresentedFrame f){Interlocked.Increment(ref shown);if(f.Kind==PresentationKind.Interpolated)Interlocked.Increment(ref generated);lock(submissions){submissions.Add(host.LastSubmissionMs);presentationTimes.Add(Clock.Now);}}
                host.FramePresented+=Count;var timer=Stopwatch.StartNew();try{await Task.Delay(2500);}finally{host.FramePresented-=Count;}
                double seconds=timer.Elapsed.TotalSeconds,output=shown/seconds,synthetic=generated/seconds;
                double[] costs;long[] ticks;lock(submissions){costs=submissions.ToArray();ticks=presentationTimes.ToArray();}Array.Sort(costs);
                var gaps=new double[Math.Max(0,ticks.Length-1)];for(int i=0;i<gaps.Length;i++)gaps[i]=(ticks[i+1]-ticks[i])/10000.0;Array.Sort(gaps);
                int effective = host.EffectiveFrameGenerationMultiplier;
                checks[$"highRate60x{multiplier}"]=new{effectiveMultiplier=effective,presentationGapP95Ms=gaps.Length==0?0:gaps[(int)((gaps.Length-1)*.95)],presentationGapP99Ms=gaps.Length==0?0:gaps[(int)((gaps.Length-1)*.99)],drawSubmissionP95Ms=costs.Length==0?0:costs[(int)((costs.Length-1)*.95)],captureFps=(source.Frames-input)/seconds,outputFps=output,generatedFps=synthetic,status=host.FrameGenerationStatus};
                File.WriteAllText(Path.Combine(folder,"high-rate-stage.json"),JsonSerializer.Serialize(checks,new JsonSerializerOptions{WriteIndented=true}));
                Require(effective == multiplier || (multiplier == 4 && effective == 3 && host.FrameGenerationStatus.Contains("4× limited by presentation", StringComparison.Ordinal)),
                    "The high-refresh pacing fallback did not report the active multiplier.");
                Require(output>60*effective*.85,"60 FPS input did not reach the active high-rate target: "+output);
                Require(synthetic>60*(effective-1)*.55,"High-rate output mainly repeated captured frames: "+synthetic);
            }
            await main.DiagnosticDisconnect();checks["highRateDisconnect"]="pass";
        }
        finally{await main.DiagnosticClose().WaitAsync(TimeSpan.FromSeconds(5));}
        return checks;
    }
    static void CheckPrediction(Dictionary<string, object> checks)
    {
        const long period = 333_333;
        foreach (var (mode, target) in new[] { (FrameGenerationMode.Predictive60, 60), (FrameGenerationMode.Predictive120, 120), (FrameGenerationMode.Predictive180, 180) })
        {
            Require(FrameGenerationPolicy.PredictiveTarget(mode) == target && FrameGenerationPolicy.Multiplier(mode) == 1,
                "Predictive modes must retain their saved targets and use a separate cadence.");
            Require(Math.Abs(FrameGenerationPolicy.PredictionInterval(target, 240, period) * target - 10_000_000) <= target / 2,
                "Prediction did not schedule the requested target.");
        }
        Require(FrameGenerationPolicy.PredictionInterval(180, 120, period) == 83_333
            && FrameGenerationPolicy.PredictionInterval(120, 60, 166_667) == 0,
            "Prediction exceeded the monitor refresh rate or invented slots at native cadence.");

        using var renderer = new D3DPreview(0, 640, 360);
        using var first = Pattern(0);
        using var second = Pattern(16);
        using var expected = Pattern(24);
        first.Time = Clock.Now;
        second.Time = first.Time + period;
        Require(renderer.CachePredictionSource(first, period, false) && !renderer.BeginPrediction(),
            "Prediction used motion before it had two captures.");
        Require(renderer.CachePredictionSource(second, period, false), "The newest capture could not be cached.");
        renderer.RenderCachedFrame(0, PreviewUpscaler.Standard);
        Require(second.Bytes.AsSpan(0, second.Length).SequenceEqual(renderer.Readback()),
            "Prediction changed the newest real image.");

        bool MotionReady()
        {
            var wait = Stopwatch.StartNew();
            bool? ready;
            while ((ready = renderer.PredictionReady()) == null && wait.ElapsedMilliseconds < 2000)
                Thread.Sleep(1);
            Require(ready != null, "Prediction analysis did not complete.");
            return ready == true;
        }

        Require(renderer.BeginPrediction() && MotionReady(), "The motion fixture could not be predicted.");
        var frame = renderer.RenderPrediction(period / 2, period / 2, 0, PreviewUpscaler.Standard);
        var image = renderer.Readback();
        double predictionError = Error(image, expected.Bytes), sourceError = Error(second.Bytes, expected.Bytes);
        Require(frame.Kind == PresentationKind.Predicted && frame.Time == second.Time + period / 2 && predictionError < sourceError * .8,
            $"Prediction did not approach the known future image ({predictionError:F2} vs {sourceError:F2}).");
        for (int y = 30; y < 68; y++)
            Require(image.AsSpan((y * 640 + 40) * 4, 160 * 4).SequenceEqual(second.Bytes.AsSpan((y * 640 + 40) * 4, 160 * 4)),
                "Prediction moved static HUD pixels.");

        using var cut = new Packet(640 * 360 * 4, width: 640, height: 360) { Time = second.Time + period, Duration = period };
        cut.Bytes.AsSpan(0, cut.Length).Fill(255);
        Require(renderer.CachePredictionSource(cut, period, false) && renderer.BeginPrediction() && !MotionReady(),
            "Prediction accepted an abrupt scene cut.");
        checks["predictivePreview"] = new { predictionError, sourceError, staticHud = "pass", sceneCut = "pass" };
    }

    static double Error(byte[] image, byte[] expected)
    {
        long sum = 0, count = 0;
        for (int y = 80; y < 328; y++) for (int x = 40; x < 600; x++)
        for (int channel = 0; channel < 3; channel++) { sum += Math.Abs(image[(y * 640 + x) * 4 + channel] - expected[(y * 640 + x) * 4 + channel]); count++; }
        return sum / (double)count;
    }
    internal static Packet Pattern(int shift, int width = 640, int height = 360)
    {
        var packet = new Packet(width * height * 4, false, width, height) { Duration = 333333 }; var bytes = packet.Bytes;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int sx = (x - shift % width + width) % width;
            uint hash = unchecked((uint)((sx / 8) * 73856093 ^ (y / 8) * 19349663)); hash ^= hash >> 13; hash *= 1274126177; hash ^= hash >> 16;
            int offset = (y * width + x) * 4; bytes[offset] = (byte)(32 + (hash & 127)); bytes[offset + 1] = (byte)(32 + ((hash >> 8) & 127)); bytes[offset + 2] = (byte)(32 + ((hash >> 16) & 127)); bytes[offset + 3] = 255;
            if (x >= 40 && x < 200 && y >= 30 && y < 68)
            {
                byte hud = (byte)((x % 12 < 3 && y > 36 && y < 60) ? 240 : 16); bytes[offset] = bytes[offset + 1] = bytes[offset + 2] = hud;
            }
        }
        return packet;
    }
}
