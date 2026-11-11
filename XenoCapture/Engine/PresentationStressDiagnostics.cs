using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using XenoCapture.UI;

namespace XenoCapture.Engine;
internal static class PresentationStressDiagnostics
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeWaitHandle CreateWaitableTimerEx(nint attributes, string? name, uint flags, uint access);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period, nint callback, nint argument, [MarshalAs(UnmanagedType.Bool)] bool resume);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForMultipleObjects(uint count, nint[] handles, [MarshalAs(UnmanagedType.Bool)] bool all, uint milliseconds);
    static async Task WaitFor(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.ElapsedMilliseconds > 5000) throw new TimeoutException("Presentation check timed out."); await Task.Delay(5); }
    }
    static long Submit(Direct3DHost host, int index = 0)
    {
        using var frame = TestSignal.Frame(index, 320, 180);
        frame.Time = Clock.Now; frame.Duration = 166_667;
        host.Submit(frame); return frame.Time;
    }
    public static async Task Run(string folder, bool includeSoak = true, int cycles = 20, PreviewUpscaler upscaler = PreviewUpscaler.Standard)
    {
        Directory.CreateDirectory(folder); int bufferBaseline = Packet.LiveBuffers;
        var checks = new Dictionary<string, object>();
        CheckSourcePacingPolicy();
        checks["sourcePacingPolicy"] = "pass";
        checks["steadyFullscreenCadencePolicy"] = CheckSteadyFullscreenCadence();
        void Save() => File.WriteAllText(Path.Combine(folder, "results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        void Stage(string text) => File.WriteAllText(Path.Combine(folder, "stage.txt"), text);
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--startup-latency-only") >= 0)
        {
            Stage("preview startup");
            var cold = new List<double>();
            var warmed = new List<double>();
            for (int trial = 0; trial < 3; trial++)
            {
                cold.Add(await MeasurePreviewStartup(warmup: false));
                warmed.Add(await MeasurePreviewStartup(warmup: true));
            }
            await WaitFor(() => Direct3DHost.LiveRenderers == 0 && Packet.LiveBuffers == bufferBaseline);
            checks["coldFirstPresentMs"] = cold;
            checks["overlappedFirstPresentMs"] = warmed;
            checks["coldMedianMs"] = cold.Order().ElementAt(1);
            checks["overlappedMedianMs"] = warmed.Order().ElementAt(1);
            checks["inputOpenDelayMs"] = 200;
            checks["measuresMonitorScanout"] = false;
            checks["result"] = "PASS";
            Stage("complete");
            Save();
            return;
        }
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--ai-motion-pacing") >= 0)
        {
            Stage("4K Ultra fullscreen motion pacing");
            checks["aiMotionPlayback"] = await RunNativePlayback(aiMotion: true);
            checks["result"] = "PASS";
            Stage("complete");
            Save();
            return;
        }
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--steady-pacing-only") >= 0)
        {
            Stage("steady fullscreen source cadence");
            checks["steadyFullscreenPlayback"] = await RunNativePlayback(steadyOnly: true);
            checks["result"] = "PASS";
            Stage("complete");
            Save();
            return;
        }
        Stage("readiness race regressions");
        using (var host = new Direct3DHost { Active = true })
        {
            var window = new PreviewWindow(host) { Title = "Xeno Capture — presentation regression", Width = 640, Height = 360, ShowActivated = false };
            long presented = 0, lastPresentedTime = 0; Exception? failure = null;
            host.FramePresented += frame => { Interlocked.Exchange(ref lastPresentedTime, frame.Time); Interlocked.Increment(ref presented); }; host.Failed += ex => failure = ex;
            window.Show();
            async Task<double> NextFrame()
            {
                long before = Interlocked.Read(ref presented); var clock = Stopwatch.StartNew(); Submit(host);
                await WaitFor(() => Interlocked.Read(ref presented) > before || failure != null);
                if (failure != null) throw failure;
                return clock.Elapsed.TotalMilliseconds;
            }
            try
            {
                // A pending event is a notification, not ownership of a packet.
                host.DiagnosticWake(); await WaitFor(() => host.EmptyWakeups > 0);
                double idleWake = await NextFrame();
                checks["redundantWakeToPresentMs"] = idleWake; checks["frameWaitTimeouts"] = host.FrameWaitTimeouts; Save();
                if (idleWake > 70 || host.FrameWaitTimeouts != 0) throw new InvalidOperationException($"A notification without a frame starved DXGI readiness: {idleWake:F1} ms.");

                long skipped = host.SkippedAfterWait;
                host.DiagnosticBeforeFrameLatch = () => { host.DiagnosticBeforeFrameLatch = null; host.Active = false; };
                Submit(host); await WaitFor(() => host.SkippedAfterWait > skipped);
                host.Active = true;
                double resumed = await NextFrame(); checks["pauseAfterReadinessToResumeMs"] = resumed; Save();
                if (resumed > 70 || host.FrameWaitTimeouts != 0) throw new InvalidOperationException($"Pausing after acquiring readiness starved the resumed frame: {resumed:F1} ms.");

                long before = Interlocked.Read(ref presented), replacementTime = 0;
                host.DiagnosticBeforeFrameLatch = () => { host.DiagnosticBeforeFrameLatch = null; replacementTime = Submit(host, 1); };
                Submit(host); await WaitFor(() => Interlocked.Read(ref presented) > before);
                if (Interlocked.Read(ref lastPresentedTime) != replacementTime) throw new InvalidOperationException("The renderer presented a superseded frame after waiting.");
                checks["latestFrameLatching"] = "pass";
                double burst = await NextFrame(); checks["replacementDuringWaitToNextPresentMs"] = burst;
                checks["frameWaitTimeouts"] = host.FrameWaitTimeouts; Save();
                if (burst > 70 || host.FrameWaitTimeouts != 0) throw new InvalidOperationException($"Replacing the pending frame left a stale notification that stalled the next frame: {burst:F1} ms.");

                long newest = 0, firstAfterDraw = 0;
                void RecordFirst(PresentedFrame frame) => Interlocked.CompareExchange(ref firstAfterDraw, frame.Time, 0);
                host.FramePresented += RecordFirst;
                try
                {
                    host.DiagnosticAfterSourceDraw = () =>
                    {
                        host.DiagnosticAfterSourceDraw = null;
                        Thread.Sleep(25);
                        newest = Submit(host, 2);
                    };
                    Submit(host);
                    await WaitFor(() => Interlocked.Read(ref firstAfterDraw) != 0 || failure != null);
                    if (failure != null) throw failure;
                    if (Interlocked.Read(ref firstAfterDraw) != newest)
                        throw new InvalidOperationException("A slow draw presented the stale capture ahead of its replacement.");
                    checks["latestFrameAfterSlowDraw"] = "pass";
                }
                finally { host.FramePresented -= RecordFirst; }
            }
            finally
            {
                host.DiagnosticBeforeFrameLatch = null;
                host.DiagnosticAfterSourceDraw = null;
                await window.CloseAsync();
            }
        }
        if (!includeSoak)
        {
            await WaitFor(() => Direct3DHost.LiveRenderers == 0 && Packet.LiveBuffers == bufferBaseline);
            checks["usesPhysicalCaptureCard"] = false; checks["measuresMonitorScanout"] = false;
            checks["result"] = "PASS"; Stage("complete"); Save(); return;
        }
        Stage("1080p60 latest-frame playback under arrival jitter");
        checks["nativeSourcePlayback"] = await RunNativePlayback();
        Save();
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--native-pacing-only") >= 0)
        {
            checks["result"] = "PASS"; Stage("complete"); Save(); return;
        }
        Stage("1080p60 fullscreen transition soak");
        int sourceWidth = upscaler == PreviewUpscaler.Enhanced ? 1280 : 1920, sourceHeight = upscaler == PreviewUpscaler.Enhanced ? 720 : 1080;
        var source = new PacedVideoBackend(sourceWidth, sourceHeight);
        var main = new MainWindow(false, false, (_, _) => new CaptureStream(() => source, TimeSpan.FromSeconds(2)))
        { ShowActivated = false, Width = 1080, Height = 760, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var samples = new List<Dictionary<string, object>>(); var allFps = new List<double>();
        var recoveryTimes = new List<double>(); var ages = new List<double>(); object ageGate = new();
        int maxRenderers = 0; long timeouts = 0, enhancedFrames = 0;
        async Task Sample(string phase, int seconds)
        {
            var host = main.DiagnosticPreview ?? throw new InvalidOperationException("No active preview after transition.");
            long originalTimeouts = host.FrameWaitTimeouts;
            void OnPresent(PresentedFrame frame) { if (host.EnhancedUpscalingActive) System.Threading.Interlocked.Increment(ref enhancedFrames); lock (ageGate) ages.Add(Math.Max(0, (Clock.Now - frame.Time) / 10000.0)); }
            host.FramePresented += OnPresent;
            try
            {
                long before = main.DiagnosticPresented; var recovery = Stopwatch.StartNew();
                await WaitFor(() => main.DiagnosticPresented > before); recoveryTimes.Add(recovery.Elapsed.TotalMilliseconds);
                await Task.Delay(250); // Exclude the window transition from steady playback buckets.
                for (int second = 0; second < seconds; second++)
                {
                    Stage(phase + " : " + (second + 1));
                    before = main.DiagnosticPresented; long inputBefore = source.Frames; var clock = Stopwatch.StartNew();
                    await Task.Delay(1000); double elapsed = clock.Elapsed.TotalSeconds;
                    double fps = (main.DiagnosticPresented - before) / elapsed, input = (source.Frames - inputBefore) / elapsed;
                    maxRenderers = Math.Max(maxRenderers, Direct3DHost.LiveRenderers); allFps.Add(fps);
                    samples.Add(new() { ["phase"] = phase, ["shownFps"] = fps, ["inputFps"] = input });
                    checks["samples"] = samples; Save();
                    if (!main.DiagnosticConnected || fps < 40 || input < 45) throw new InvalidOperationException($"Playback slowed during {phase}: {fps:F1} shown / {input:F1} input FPS.");
                }
            }
            finally { host.FramePresented -= OnPresent; timeouts += host.FrameWaitTimeouts - originalTimeouts; }
        }
        main.Show();
        try
        {
            main.DiagnosticSelectUpscaler(upscaler); main.DiagnosticPrepareInput(); await main.DiagnosticConnectFullscreen(fastestMonitor: true);
            await Sample("initial fullscreen", 8);
            int baselineHandles = Process.GetCurrentProcess().HandleCount;

            for (int cycle = 0; cycle < cycles; cycle++)
            {
                await main.DiagnosticWindowed(); await Sample($"windowed {cycle + 1}", 2);
                if (Direct3DHost.LiveRenderers != 1) throw new InvalidOperationException("Closing fullscreen retained a render thread.");
                // Also resize the retained embedded swap chain between trips.
                main.Width = cycle % 2 == 0 ? 1160 : 1000;
                main.Height = cycle % 2 == 0 ? 800 : 700;
                await main.DiagnosticFullscreen(fastestMonitor: true); await Sample($"fullscreen {cycle + 1}", 2);
            }
            await main.DiagnosticWindowed(); await Sample("final windowed", 8);
            int finalHandles = Process.GetCurrentProcess().HandleCount;
            checks["fullscreenRoundTrips"] = cycles; checks["oneSecondPlaybackBuckets"] = allFps.Count;
            checks["minimumBucketFps"] = allFps.Min(); checks["averageBucketFps"] = allFps.Average();
            checks["maximumTransitionFirstFrameMs"] = recoveryTimes.Max(); checks["maximumLiveRenderers"] = maxRenderers;
            checks["frameWaitTimeoutsDuringSoak"] = timeouts; checks["handleCountBeforeCycles"] = baselineHandles; checks["handleCountAfterCycles"] = finalHandles;
            lock (ageGate) { ages.Sort(); checks["syntheticSampleAgeAtPresentP95Ms"] = ages[(int)((ages.Count - 1) * .95)]; }
        }
        finally { await main.DiagnosticClose().WaitAsync(TimeSpan.FromSeconds(5)); }
        await WaitFor(() => Direct3DHost.LiveRenderers == 0 && Packet.LiveBuffers == bufferBaseline && MediaFoundationUsage.ActiveWorkers == 0 && MultimediaScheduling.ActiveRegistrations == 0);
        checks["renderersAfterClose"] = Direct3DHost.LiveRenderers; checks["pooledBuffersAfterClose"] = Packet.LiveBuffers - bufferBaseline;
        foreach (var item in source.CadenceStats()) checks[item.Key] = item.Value;
        checks["mediaWorkersAfterClose"] = MediaFoundationUsage.ActiveWorkers; checks["mmcssRegistrationsAfterClose"] = MultimediaScheduling.ActiveRegistrations;
        if (MultimediaScheduling.FailedReverts != 0) throw new InvalidOperationException("MMCSS failed to release a preview/capture thread registration.");
        checks["usesPhysicalCaptureCard"] = false; checks["measuresPhysicalPixelResponse"] = false; checks["syntheticInput"] = $"{sourceWidth}x{sourceHeight} NV12 at 60 FPS"; checks["previewUpscaler"] = upscaler.ToString(); checks["enhancedFrames"] = System.Threading.Interlocked.Read(ref enhancedFrames);
        if (upscaler == PreviewUpscaler.Enhanced && enhancedFrames == 0) throw new InvalidOperationException("The enhanced presentation soak never ran its upscaling pipeline.");
        checks["result"] = "PASS"; Stage("complete"); Save();
    }
    static async Task<double> MeasurePreviewStartup(bool warmup)
    {
        var source = new TestCaptureBackend(delayOpen: true) { Produce = true };
        var main = new MainWindow(false, false, (_, _) => new CaptureStream(() => source, TimeSpan.FromSeconds(2)))
        {
            ShowActivated = false,
            DiagnosticDisablePreviewWarmup = !warmup
        };
        main.DiagnosticPrepareInput();
        main.Show();
        try
        {
            Task connecting = main.DiagnosticConnect();
            await source.OpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            await Task.Delay(200);
            var timer = Stopwatch.StartNew();
            source.ReleaseBlockedCalls();
            await connecting.WaitAsync(TimeSpan.FromSeconds(4));
            await WaitFor(() => main.DiagnosticPresented > 0 || main.DiagnosticPreviewError != null);
            if (main.DiagnosticPreviewError != null) throw main.DiagnosticPreviewError;
            return timer.Elapsed.TotalMilliseconds;
        }
        finally
        {
            source.ReleaseBlockedCalls();
            await main.DiagnosticClose().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    static void CheckSourcePacingPolicy()
    {
        const long period = 166_667, sourceTime = 1_000_000, arrival = 1_100_000;
        var pacing = new SourceFramePacing(sourceTime, period, arrival);
        long early = arrival + period - 30_000;
        if (pacing.Deadline(sourceTime + period, period, early, 240) != early + 20_000
            || pacing.Deadline(sourceTime + period, period, early, 60) != early
            || pacing.Deadline(sourceTime + period, period, early, 360) != early + 13_888
            || pacing.Deadline(sourceTime + period, period, early, 180, useDisplayBudget: true) != early + 30_000)
            throw new InvalidOperationException("Source pacing did not respect the hold and display limits.");

        long late = arrival + period + 30_000, burst = arrival + period / 2;
        if (pacing.Deadline(sourceTime + period, period, late, 240) != late
            || pacing.Deadline(sourceTime + period, period, burst, 240) != burst
            || pacing.Deadline(sourceTime + period * 2, period, early, 240) != early
            || pacing.Deadline(sourceTime, period, early, 240) != early
            || pacing.Deadline(sourceTime + period, period / 2, early, 240) != early)
            throw new InvalidOperationException("Source pacing held a late frame, burst, or discontinuity.");

        const long lastDisplayed = arrival + 35_000, nextArrival = arrival + period - 30_000;
        if (pacing.DisplayDeadline(sourceTime + period, period, nextArrival, 240, lastDisplayed) != arrival + 163_500
            || pacing.DisplayDeadline(sourceTime + period, period, nextArrival, 180, arrival + 55_000) != arrival + 169_611
            || pacing.DisplayDeadline(sourceTime + period, period, nextArrival, 180, arrival + 55_000, drawAhead: true) != arrival + 180_000
            || pacing.DisplayDeadline(sourceTime + period, period, nextArrival, 144, lastDisplayed) != null
            || pacing.DisplayDeadline(sourceTime + period, period, nextArrival, 240, arrival - 1) != null
            || pacing.DisplayDeadline(sourceTime + period, period, late, 240, lastDisplayed) != late
            || pacing.DisplayDeadline(sourceTime + period, period, late, 180, arrival + 90_000) != arrival + 204_611
            || pacing.DisplayDeadline(sourceTime + period, period, early, 180, arrival + 65_000) != arrival + 179_611
            || pacing.DisplayDeadline(sourceTime + period, period, early, 180, arrival + 80_000) != early)
            throw new InvalidOperationException("Display pacing did not respect the refresh phase or hold limit.");
    }

    static object CheckSteadyFullscreenCadence()
    {
        const long period = 166_667, baseTime = 1_000_000, lag = 8_000, jitter = 30_000;
        if (FullscreenSourceCadence.RefreshInterval(period, 144) != 0 || FullscreenSourceCadence.RefreshInterval(period, 0) != 0
            || FullscreenSourceCadence.RefreshInterval(period, 60) != 0
            || FullscreenSourceCadence.RefreshInterval(166_833, 180) != 3 || FullscreenSourceCadence.RefreshInterval(166_833, 240) != 4)
            throw new InvalidOperationException("Steady pacing accepted an incompatible refresh rate or rejected 59.94 FPS.");
        var cases = new List<object>();
        foreach (double hz in new[] { 120d, 180d, 240d })
        {
            if (FullscreenSourceCadence.RefreshInterval(period, hz) != (uint)Math.Round(hz / 60))
                throw new InvalidOperationException("Steady pacing rejected a matching refresh rate.");
            FullscreenSourceCadence cadence = default;
            long previousDue = 0, maxDeviation = 0, maxHold = 0;
            long holdLimit = Math.Min(60_000, (long)(10_000_000 / hz));
            for (int i = 0; i < 240; i++)
            {
                long time = baseTime + i * period;
                long arrival = time + lag + (i % 2) * jitter;
                long due = cadence.Deadline(time, period, arrival, hz, out _);
                if (due < arrival || due > arrival + holdLimit)
                    throw new InvalidOperationException("Steady pacing exceeded its hold limit.");
                if (i > 60) maxDeviation = Math.Max(maxDeviation, Math.Abs(due - previousDue - period));
                maxHold = Math.Max(maxHold, due - arrival);
                previousDue = due;
            }
            if (maxDeviation > 8_000)
                throw new InvalidOperationException("Steady pacing did not smooth alternating input jitter.");
            long missedFrame = baseTime + 241 * period;
            if (cadence.Deadline(missedFrame, period, missedFrame + lag, hz, out long steadySubmitBy) != missedFrame + lag || steadySubmitBy > missedFrame + lag)
                throw new InvalidOperationException("Steady pacing held a frame after a dropped input.");
            const long delay = 120_000;
            FullscreenSourceCadence lateDelivery = default;
            lateDelivery.Deadline(baseTime, period, baseTime + lag, hz, out _);
            long late = baseTime + period + lag + delay;
            if (lateDelivery.Deadline(baseTime + period, period, late, hz, out steadySubmitBy) != late || steadySubmitBy > late)
                throw new InvalidOperationException("A late source frame should be shown immediately.");
            long resumed = baseTime + 2 * period + lag;
            long resumedDue = lateDelivery.Deadline(baseTime + 2 * period, period, resumed, hz, out steadySubmitBy);
            if (steadySubmitBy <= resumedDue || resumedDue <= resumed || resumedDue > resumed + holdLimit)
                throw new InvalidOperationException("A single late frame disrupted the next on-time frame.");

            // A permanent delivery shift must still establish a new clock.
            lateDelivery = default;
            lateDelivery.Deadline(baseTime, period, baseTime + lag, hz, out _);
            lateDelivery.Deadline(baseTime + period, period, late, hz, out _);
            lateDelivery.Deadline(baseTime + 2 * period, period, late + period, hz, out steadySubmitBy);
            if (steadySubmitBy > late + period) throw new InvalidOperationException("Steady pacing did not catch up to a changed delivery phase.");
            lateDelivery.Deadline(baseTime + 3 * period, period, late + 2 * period, hz, out steadySubmitBy);
            if (steadySubmitBy <= late + 2 * period) throw new InvalidOperationException("Steady pacing did not settle on the new delivery phase.");

            // This delivery is late enough to consume the draw budget, but still
            // close enough to follow the existing capture phase.
            lateDelivery = default;
            lateDelivery.Deadline(baseTime, period, baseTime + lag, hz, out _);
            long refreshPeriod = (long)(10_000_000 / hz);
            long lateArrival = baseTime + period + lag + Math.Min(period / 2 - 1, refreshPeriod * 4 / 3);
            long release = lateDelivery.Deadline(baseTime + period, period, lateArrival, hz, out steadySubmitBy);
            long submittedAt = lateArrival + holdLimit * 4 / 5;
            if (release != lateArrival || steadySubmitBy <= lateArrival || submittedAt < steadySubmitBy || submittedAt >= release + refreshPeriod)
                throw new InvalidOperationException("Late delivery and drawing did not share the same refresh budget.");
            cases.Add(new { hz, maximumScheduledGapErrorMs = maxDeviation / 10000.0, maximumHoldMs = maxHold / 10000.0 });
        }
        return cases;
    }

    static async Task<Dictionary<string, object>> RunNativePlayback(bool aiMotion = false, bool steadyOnly = false)
    {
        const long period = 166667, arrivalJitter = 30_000;
        bool steadyAi = steadyOnly && Array.IndexOf(Environment.GetCommandLineArgs(), "--steady-pacing-ai") >= 0;
        var source = new PacedVideoBackend(1920, 1080, arrivalJitter, pan: aiMotion || steadyOnly);
        var main = new MainWindow(false, false, (_, _) => new CaptureStream(() => source, TimeSpan.FromSeconds(2)))
            { ShowActivated = false };
        main.Show();
        try
        {
            main.DiagnosticPrepareInput();
            if (aiMotion)
            {
                main.DiagnosticSelectAiVideo(true, 2160, 4);
                main.DiagnosticSelectFrameGeneration(FrameGenerationMode.RecoverGaps);
                await main.DiagnosticConnectFullscreen(fastestMonitor: true);
                await WaitFor(() => main.DiagnosticPreview?.AiVideoActive == true);
            }
            else await main.DiagnosticConnect();
            async Task<Dictionary<string, double>> Sample(Direct3DHost host)
            {
                await Task.Delay(350);
                var frames = new List<(long At, long Time)>();
                var displayed = new List<(uint Present, uint Refresh)>();
                void OnDisplay(uint present, uint refresh)
                {
                    lock (frames)
                    {
                        if (displayed.Count == 0 || displayed[^1].Present != present)
                            displayed.Add((present, refresh));
                    }
                }
                void OnPresent(PresentedFrame frame)
                {
                    if (frame.Kind == PresentationKind.Source)
                        lock (frames) frames.Add((Clock.Now, frame.Time));
                }
                host.FramePresented += OnPresent;
                host.DiagnosticDisplayFrame += OnDisplay;
                long initialPacingWaits = host.SourcePacingWaits;
                var elapsed = Stopwatch.StartNew();
                try { await Task.Delay(aiMotion || steadyOnly ? 6000 : 3000); }
                finally
                {
                    host.FramePresented -= OnPresent;
                    host.DiagnosticDisplayFrame -= OnDisplay;
                }
                (long At, long Time)[] sample;
                double[] refreshGaps;
                int displaySamples;
                double refreshHz = DisplayTiming.RefreshRate(host.Handle);
                lock (frames)
                {
                    sample = frames.ToArray();
                    displaySamples = displayed.Count;
                    refreshGaps = displayed.Zip(displayed.Skip(1), (a, b) =>
                            unchecked(b.Present - a.Present) == 1 ? unchecked(b.Refresh - a.Refresh) : 0)
                        .Where(gap => gap > 0 && gap <= refreshHz)
                        .Select(gap => (double)gap).Order().ToArray();
                }
                double fps = sample.Length / elapsed.Elapsed.TotalSeconds;
                if (fps < 55 || (!aiMotion && host.GeneratedPresentations != 0))
                    throw new InvalidOperationException($"Native preview was not presenting the captured source at 60 FPS ({fps:F2}).");
                var gaps = sample.Zip(sample.Skip(1), (a, b) => (b.At - a.At) / 10000.0).ToArray();
                var ages = sample.Select(frame => (frame.At - frame.Time) / 10000.0).Order().ToArray();
                var deviations = gaps.Select(gap => Math.Abs(gap - period / 10000.0)).Order().ToArray();
                double At(double[] sorted, double percentile) => sorted[(int)((sorted.Length - 1) * percentile)];
                var report = new Dictionary<string, double>
                {
                    ["fps"] = fps, ["gapDeviationP95Ms"] = At(deviations, .95),
                    ["sourcePresentations"] = sample.Length,
                    ["missingSourceFrames"] = sample.Zip(sample.Skip(1), (a, b) =>
                        Math.Max(0, Math.Round((b.Time - a.Time) / (double)period) - 1)).Sum(),
                    ["gapP99Ms"] = At(gaps.Order().ToArray(), .99), ["sampleAgeP50Ms"] = At(ages, .50),
                    ["sampleAgeP95Ms"] = At(ages, .95), ["frameWaitTimeouts"] = host.FrameWaitTimeouts,
                    ["sourcePacingWaits"] = host.SourcePacingWaits - initialPacingWaits,
                    ["displayRefreshHz"] = refreshHz,
                    ["dxgiDisplaySamples"] = displaySamples,
                    ["dxgiRefreshGapSamples"] = refreshGaps.Length
                };
                // Windowed and mixed-monitor paths can return sparse DXGI samples.
                if (refreshGaps.Length >= sample.Length * .75)
                {
                    int expected = (int)Math.Round(refreshHz * period / 10_000_000);
                    report["dxgiRefreshGapP50"] = At(refreshGaps, .50);
                    report["dxgiRefreshGapP95"] = At(refreshGaps, .95);
                    report["dxgiRefreshGapP99"] = At(refreshGaps, .99);
                    report["dxgiNearestCadencePercent"] = 100.0 * refreshGaps.Count(gap => gap == expected) / refreshGaps.Length;
                }
                return report;
            }
            if (aiMotion)
            {
                var aiHost = main.DiagnosticPreview ?? throw new InvalidOperationException("No fullscreen AI preview host.");
                var displayBounds = System.Windows.Forms.Screen.FromHandle(aiHost.Handle).Bounds;
                var sample = await Sample(aiHost);
                if (!aiHost.AiVideoActive)
                    throw new InvalidOperationException("AI video became unavailable during the motion sample.");
                return new()
                {
                    ["fullscreenWidth"] = displayBounds.Width, ["fullscreenHeight"] = displayBounds.Height,
                    ["aiVideo"] = aiHost.AiVideoStatus, ["injectedAlternateDelayMs"] = arrivalJitter / 10000.0,
                    ["fullscreen"] = sample
                };
            }
            if (steadyOnly)
            {
                await WaitFor(() => main.DiagnosticHasFrame);
                if (steadyAi) main.DiagnosticSelectAiVideo(true, 1440, 3);
                var displays = new List<object>();
                foreach (var screen in System.Windows.Forms.Screen.AllScreens)
                {
                    double hz = DisplayTiming.RefreshRateForDevice(screen.DeviceName);
                    if (FullscreenSourceCadence.RefreshInterval(period, hz) == 0) continue;
                    await main.DiagnosticFullscreenOnDisplay(screen.Bounds);
                    var host = main.DiagnosticPreview ?? throw new InvalidOperationException("No fullscreen host for steady pacing.");
                    if (steadyAi) await WaitFor(() => host.AiVideoActive);
                    var ordinary = await Sample(host);

                    main.DiagnosticSetSteadyFullscreenPacing(true);
                    string steadySettings = main.DiagnosticSettingsJson;
                    main.DiagnosticSetSteadyFullscreenPacing(false);
                    main.DiagnosticApplySettingsJson(steadySettings);
                    if (!host.SteadyFullscreenPacing || main.DiagnosticEmbedded.SteadyFullscreenPacing)
                        throw new InvalidOperationException("The saved cadence preference did not apply only to the fullscreen host.");
                    var steady = await Sample(host);
                    if (steady["sourcePacingWaits"] == 0)
                        throw new InvalidOperationException("Steady cadence never scheduled a display hold.");

                    main.DiagnosticSetSteadyFullscreenPacing(false);
                    var ordinaryRepeat = await Sample(host);
                    await main.DiagnosticWindowed();
                    displays.Add(new { hz, screen.Bounds.Width, screen.Bounds.Height, aiVideo = host.AiVideoStatus, ordinary, steady, ordinaryRepeat });
                }
                return new()
                {
                    ["displays"] = displays, ["injectedAlternateDelayMs"] = arrivalJitter / 10000.0,
                    ["measuresPhysicalPixelResponse"] = false
                };
            }
            var windowed = await Sample(main.DiagnosticPreview ?? throw new InvalidOperationException("No windowed preview host."));
            await main.DiagnosticFullscreen(fastestMonitor: true);
            var fullscreen = main.DiagnosticPreview ?? throw new InvalidOperationException("No fullscreen preview host.");
            main.DiagnosticSetSourcePacing(false);
            string immediateSettings = main.DiagnosticSettingsJson;
            main.DiagnosticSetSourcePacing(true);
            main.DiagnosticApplySettingsJson(immediateSettings);
            if (fullscreen.SmoothSourcePacing || main.DiagnosticEmbedded.SmoothSourcePacing)
                throw new InvalidOperationException("The disabled source pacing setting did not reach both preview hosts.");
            var immediate = await Sample(fullscreen);
            if (immediate["sourcePacingWaits"] != 0)
                throw new InvalidOperationException("Source pacing waited with its setting off.");
            main.DiagnosticSetSourcePacing(true);
            var standard = await Sample(fullscreen);

            main.DiagnosticSelectUpscaler(PreviewUpscaler.Performance);
            string saved = main.DiagnosticSettingsJson;
            main.DiagnosticSelectUpscaler(PreviewUpscaler.Standard);
            main.DiagnosticApplySettingsJson(saved);
            if (main.DiagnosticUpscaler != PreviewUpscaler.Performance || fullscreen.Upscaler != PreviewUpscaler.Performance)
                throw new InvalidOperationException("The Performance setting did not restore to the live preview.");
            var performance = await Sample(fullscreen);
            var bounds = System.Windows.Forms.Screen.FromHandle(fullscreen.Handle).Bounds;
            return new()
            {
                ["fullscreenWidth"] = bounds.Width, ["fullscreenHeight"] = bounds.Height,
                ["windowed"] = windowed, ["fullscreen"] = standard, ["fullscreenImmediate"] = immediate, ["fullscreenPerformance"] = performance,
                ["performancePreferenceRoundTrip"] = "pass", ["injectedAlternateDelayMs"] = arrivalJitter / 10000.0,
                ["measuresPhysicalPixelResponse"] = false
            };
        }
        finally { await main.DiagnosticClose().WaitAsync(TimeSpan.FromSeconds(5)); }
    }
    sealed class PacedVideoBackend(int width, int height, long arrivalJitter = 0, bool pan = false) : ICaptureBackend
    {
        readonly ManualResetEvent stopped = new(false);
        SafeWaitHandle? timer; readonly nint[] waitHandles = new nint[2];
        readonly object shutdownGate = new(); bool disposed;
        long next, origin, frames, previous;
        readonly List<double> intervals = new();
        byte[]? panPattern;
        public long Frames => Interlocked.Read(ref frames);
        public PixelLayout Layout => PixelLayout.Nv12;
        public int AudioRate => 0;
        public int AudioChannels => 0;
        public void Open(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            timer = CreateWaitableTimerEx(0, null, 2, 0x100002);
            if (timer.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            waitHandles[0] = stopped.SafeWaitHandle.DangerousGetHandle(); waitHandles[1] = timer.DangerousGetHandle();
            if (pan)
            {
                panPattern = new byte[width * height];
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        panPattern[y * width + x] = (byte)(16 + (17 * ((x / 13 + y / 9) % 13) + 5 * (x / 47 ^ y / 29)) % 220);
            }
            origin = next = Clock.Now;
        }
        public Packet? Read()
        {
            long timestamp = arrivalJitter == 0 ? next : origin + Frames * 166667;
            long extra = arrivalJitter != 0 && Frames % 2 == 1 ? arrivalJitter : 0;
            long delay = timestamp + extra - Clock.Now;
            if (delay > 0)
            {
                long due = -delay;
                if (!SetWaitableTimer(timer!, ref due, 0, 0, 0, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
                uint result = WaitForMultipleObjects(2, waitHandles, false, 2000);
                if (result == 0) return null;
                if (result != 1) throw new Win32Exception(Marshal.GetLastWin32Error(), "Synthetic frame timer wait failed.");
            }
            if (stopped.WaitOne(0)) return null;
            long now = Clock.Now; next = Math.Max(next + 166667, now);
            if (previous != 0) intervals.Add((now - previous) / 10000.0); previous = now;
            long index = Interlocked.Increment(ref frames);
            var frame = new Packet(width * height * 3 / 2, false, width, height, PixelLayout.Nv12) { Time = arrivalJitter == 0 ? now : timestamp, Duration = 166667 };
            if (panPattern is { } pattern)
            {
                int shift = (int)(index * 19 % width);
                for (int y = 0; y < height; y++)
                {
                    int row = y * width;
                    pattern.AsSpan(row + shift, width - shift).CopyTo(frame.Bytes.AsSpan(row));
                    pattern.AsSpan(row, shift).CopyTo(frame.Bytes.AsSpan(row + width - shift));
                }
            }
            else Array.Fill(frame.Bytes, (byte)(index % 100 + 60), 0, width * height);
            Array.Fill(frame.Bytes, (byte)128, width * height, width * height / 2);
            return frame;
        }
        internal Dictionary<string, object> CadenceStats()
        {
            intervals.Sort();
            return new() { ["syntheticInputIntervalP1Ms"] = intervals[(int)((intervals.Count - 1) * .01)], ["syntheticInputIntervalP95Ms"] = intervals[(int)((intervals.Count - 1) * .95)],
                ["syntheticInputIntervalP99Ms"] = intervals[(int)((intervals.Count - 1) * .99)], ["syntheticInputBurstsUnder4ms"] = intervals.Count(x => x < 4) };
        }
        public void Shutdown() { lock (shutdownGate) { if (!disposed) stopped.Set(); } }
        public void Dispose() { lock (shutdownGate) { if (disposed) return; stopped.Set(); timer?.Dispose(); stopped.Dispose(); disposed = true; } }
    }
}
