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
        using var frame = TestSignal.Frame(index, 320, 180); frame.Time = Clock.Now; host.Submit(frame); return frame.Time;
    }
    public static async Task Run(string folder, bool includeSoak = true, int cycles = 20, PreviewUpscaler upscaler = PreviewUpscaler.Standard)
    {
        Directory.CreateDirectory(folder); int bufferBaseline = Packet.LiveBuffers;
        var checks = new Dictionary<string, object>();
        void Save() => File.WriteAllText(Path.Combine(folder, "results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        void Stage(string text) => File.WriteAllText(Path.Combine(folder, "stage.txt"), text);
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

                long empty = host.EmptyWakeups, before = Interlocked.Read(ref presented), replacementTime = 0;
                host.DiagnosticBeforeFrameLatch = () => { host.DiagnosticBeforeFrameLatch = null; replacementTime = Submit(host, 1); };
                Submit(host); await WaitFor(() => Interlocked.Read(ref presented) > before && host.EmptyWakeups > empty);
                if (Interlocked.Read(ref lastPresentedTime) != replacementTime) throw new InvalidOperationException("The renderer presented a superseded frame after waiting.");
                checks["latestFrameLatching"] = "pass";
                double burst = await NextFrame(); checks["replacementDuringWaitToNextPresentMs"] = burst;
                checks["frameWaitTimeouts"] = host.FrameWaitTimeouts; Save();
                if (burst > 70 || host.FrameWaitTimeouts != 0) throw new InvalidOperationException($"Replacing the pending frame left a stale notification that stalled the next frame: {burst:F1} ms.");
            }
            finally { host.DiagnosticBeforeFrameLatch = null; await window.CloseAsync(); }
        }
        if (!includeSoak)
        {
            await WaitFor(() => Direct3DHost.LiveRenderers == 0 && Packet.LiveBuffers == bufferBaseline);
            checks["usesPhysicalCaptureCard"] = false; checks["measuresMonitorScanout"] = false;
            checks["result"] = "PASS"; Stage("complete"); Save(); return;
        }
        Stage("1080p60 native fullscreen pacing under arrival jitter");
        checks["nativeFullscreenPacing"] = await RunNativeCadence();
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
            main.DiagnosticSelectUpscaler(upscaler); main.DiagnosticPrepareInput(); await main.DiagnosticConnectFullscreen(maximize: upscaler == PreviewUpscaler.Enhanced);
            await Sample("initial fullscreen", 8);
            int baselineHandles = Process.GetCurrentProcess().HandleCount;

            for (int cycle = 0; cycle < cycles; cycle++)
            {
                await main.DiagnosticWindowed(); await Sample($"windowed {cycle + 1}", 2);
                if (Direct3DHost.LiveRenderers != 1) throw new InvalidOperationException("Closing fullscreen retained a render thread.");
                // Also resize the retained embedded swap chain between trips.
                main.Width = cycle % 2 == 0 ? 1160 : 1000;
                main.Height = cycle % 2 == 0 ? 800 : 700;
                await main.DiagnosticFullscreen(); await Sample($"fullscreen {cycle + 1}", 2);
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
        checks["usesPhysicalCaptureCard"] = false; checks["measuresMonitorScanout"] = false; checks["syntheticInput"] = $"{sourceWidth}x{sourceHeight} NV12 at 60 FPS"; checks["previewUpscaler"] = upscaler.ToString(); checks["enhancedFrames"] = System.Threading.Interlocked.Read(ref enhancedFrames);
        if (upscaler == PreviewUpscaler.Enhanced && enhancedFrames == 0) throw new InvalidOperationException("The enhanced presentation soak never ran its upscaling pipeline.");
        checks["result"] = "PASS"; Stage("complete"); Save();
    }
    static async Task<Dictionary<string, object>> RunNativeCadence()
    {
        const long period = 166667, arrivalJitter = 30_000;
        var source = new PacedVideoBackend(1920, 1080, arrivalJitter);
        var main = new MainWindow(false, false, (_, _) => new CaptureStream(() => source, TimeSpan.FromSeconds(2)))
            { ShowActivated = false };
        main.Show();
        try
        {
            main.DiagnosticPrepareInput();
            await main.DiagnosticConnectFullscreen();
            var host = main.DiagnosticPreview ?? throw new InvalidOperationException("No native fullscreen host.");
            async Task<Dictionary<string, double>> Sample(bool disabled)
            {
                host.DiagnosticDisableSourcePacing = disabled;
                await Task.Delay(350);
                var frames = new List<(long At, long Time)>();
                void OnPresent(PresentedFrame frame) { lock (frames) frames.Add((Clock.Now, frame.Time)); }
                host.FramePresented += OnPresent;
                var elapsed = Stopwatch.StartNew();
                try { await Task.Delay(3000); }
                finally { host.FramePresented -= OnPresent; }
                (long At, long Time)[] sample;
                lock (frames) sample = frames.ToArray();
                double fps = sample.Length / elapsed.Elapsed.TotalSeconds;
                if (fps < 55 || host.GeneratedPresentations != 0)
                    throw new InvalidOperationException($"Native fullscreen was not presenting the captured source at 60 FPS ({fps:F2}).");
                var gaps = sample.Zip(sample.Skip(1), (a, b) => (b.At - a.At) / 10000.0).ToArray();
                var ages = sample.Select(frame => (frame.At - frame.Time) / 10000.0).Order().ToArray();
                var deviations = gaps.Select(gap => Math.Abs(gap - period / 10000.0)).Order().ToArray();
                double At(double[] sorted, double percentile) => sorted[(int)((sorted.Length - 1) * percentile)];
                return new()
                {
                    ["fps"] = fps, ["gapDeviationP95Ms"] = At(deviations, .95),
                    ["gapP99Ms"] = At(gaps.Order().ToArray(), .99), ["sampleAgeP50Ms"] = At(ages, .50),
                    ["sampleAgeP95Ms"] = At(ages, .95)
                };
            }
            var unpaced = await Sample(true);
            var paced = await Sample(false);
            return new() { ["unpaced"] = unpaced, ["paced"] = paced, ["injectedAlternateDelayMs"] = arrivalJitter / 10000.0 };
        }
        finally { await main.DiagnosticClose().WaitAsync(TimeSpan.FromSeconds(5)); }
    }
    sealed class PacedVideoBackend(int width, int height, long arrivalJitter = 0) : ICaptureBackend
    {
        readonly ManualResetEvent stopped = new(false);
        SafeWaitHandle? timer; readonly nint[] waitHandles = new nint[2];
        readonly object shutdownGate = new(); bool disposed;
        long next, origin, frames, previous;
        readonly List<double> intervals = new();
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
            Array.Fill(frame.Bytes, (byte)(index % 100 + 60), 0, width * height); Array.Fill(frame.Bytes, (byte)128, width * height, width * height / 2);
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
