using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using XenoCapture.UI;

namespace XenoCapture.Engine;
internal static class PresentationDiagnostics
{
    public static async Task<Dictionary<string, object>> Run(CaptureDevice? device = null, VideoMode? mode = null, Action<string>? progress = null)
    {
        using var host = new Direct3DHost { Active = true, Sharpness = 0.25 };
        var window = new PreviewWindow(host) { Title = "Xeno Capture — checking video smoothness", Width = 660, Height = 420, WindowStyle = WindowStyle.ToolWindow, ShowActivated = false, Topmost = true, Content = host, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        long displayed = 0; Exception? failure = null;
        var intervals = new List<double>(); var transit = new List<double>(); var sync = new object();
        long previous = 0; bool measure = false;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Failed += ex => { failure = ex; first.TrySetException(ex); };
        host.FramePresented += frame =>
        {
            Interlocked.Increment(ref displayed); first.TrySetResult();
            lock (sync)
            {
                if (measure)
                {
                    long now = Stopwatch.GetTimestamp();
                    if (previous != 0) intervals.Add(Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds);
                    previous = now; transit.Add(Math.Max(0, (Clock.Now - frame.Time) / 10000.0));
                }
            }
        };
        window.Show();
        CaptureStream? capture = device == null ? null : new CaptureStream(device, mode, nativeVideo: true);
        long submitted = 0;
        if (capture != null)
        {
            capture.Progress += step => progress?.Invoke(step);
            capture.Data += frame => { Interlocked.Increment(ref submitted); host.Submit(frame); };
            capture.Failed += ex => { failure = ex; first.TrySetException(ex); };
        }
        using var cancel = new CancellationTokenSource();
        Task generator = capture != null ? Task.CompletedTask : Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1)); long next = Clock.Now;
            try
            {
                while (await timer.WaitForNextTickAsync(cancel.Token))
                {
                    if (Clock.Now < next) continue; next += 166667; Interlocked.Increment(ref submitted);
                    using var frame = new Packet(1280 * 720 * 3 / 2, false, 1280, 720, PixelLayout.Nv12) { Time = Clock.Now, Duration = 166667 };
                    Array.Fill(frame.Bytes, (byte)120, 0, 1280 * 720); Array.Fill(frame.Bytes, (byte)128, 1280 * 720, 1280 * 360); host.Submit(frame);
                }
            }
            catch (OperationCanceledException) { }
        });
        try
        {
            progress?.Invoke("starting capture"); if (capture != null) await capture.Start(); progress?.Invoke("waiting for first frame");
            await first.Task.WaitAsync(TimeSpan.FromSeconds(8)); progress?.Invoke("presenting frames"); await Task.Delay(250);
            lock (sync) measure = true;
            long initial = Interlocked.Read(ref displayed), inputInitial = Interlocked.Read(ref submitted); var clock = Stopwatch.StartNew();
            await Task.Delay(800);
            long beforeStall = Interlocked.Read(ref displayed);
            Thread.Sleep(300); // Deliberately block only WPF; the video thread must keep presenting.
            long duringStall = Interlocked.Read(ref displayed) - beforeStall;
            await Task.Delay(1900); double seconds = clock.Elapsed.TotalSeconds;
            long frames = Interlocked.Read(ref displayed) - initial;
            if (failure != null) throw failure;
            if (frames < 120 || duringStall < 8) throw new InvalidOperationException($"Video presentation stalled: {frames} frames, {duringStall} during a 300 ms UI pause.");
            lock (sync)
            {
                intervals.Sort(); transit.Sort();
                return new()
                {
                    ["inputFrames"] = Interlocked.Read(ref submitted) - inputInitial, ["videoLayout"] = capture?.Layout.ToString() ?? "Nv12",
                    ["presentedFrames"] = frames, ["presentationFps"] = frames / seconds,
                    ["framesDuring300msUiPause"] = duringStall,
                    ["presentationIntervalP95Ms"] = intervals[(int)((intervals.Count - 1) * .95)],
                    [capture == null ? "syntheticEnqueueToPresentP95Ms" : "estimatedSampleAgeAtPresentP95Ms"] = transit[(int)((transit.Count - 1) * .95)],
                    ["presentationTestUsesPhysicalCaptureCard"] = capture != null,
                    ["presentationTestMeasuresButtonToScreenLatency"] = false
                };
            }
        }
        catch (Exception ex) { failure ??= ex; throw; }
        finally
        {
            progress?.Invoke("stopping capture"); cancel.Cancel(); await generator;
            try { if (capture != null) await capture.Stop(); }
            catch when (failure != null) { }
            finally { await window.CloseAsync(); progress?.Invoke("complete"); }
        }
    }
}




