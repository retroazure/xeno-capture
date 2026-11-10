using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using XenoCapture.UI;

namespace XenoCapture.Engine;
internal static class LifecycleDiagnostics
{
    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static async Task WaitFor(Func<bool> condition, int milliseconds = 3000)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.ElapsedMilliseconds > milliseconds) throw new TimeoutException("Lifecycle assertion did not become true."); await Task.Delay(10); }
    }
    static async Task ExpectCancelled(Task task) { try { await task; } catch (OperationCanceledException) { return; } throw new InvalidOperationException("Stopped startup was not cancelled."); }
    static void CheckCaptureBufferLayout()
    {
        Require(MediaCaptureBackend.VideoCopyLayout.TryCreate(PixelLayout.Bgr32, 4, 4, 1_000_000_000, 3_000_000_016, out var wide)
            && wide.RowOffset(3) == 3_000_000_000L, "The capture row offset wrapped at the 32-bit boundary.");
        Require(!MediaCaptureBackend.VideoCopyLayout.TryCreate(PixelLayout.Bgr32, 4, 4, 1_000_000_000, 3_000_000_015, out _),
            "A capture buffer missing the final byte was accepted.");
        Require(MediaCaptureBackend.VideoCopyLayout.TryCreate(PixelLayout.Bgr32, 2, 2, -12, 20, out var inverted)
            && inverted.RowOffset(0) == 12 && inverted.RowOffset(1) == 0 && inverted.Length == 16,
            "Bottom-up RGB or trailing row padding was handled incorrectly.");
        Require(MediaCaptureBackend.VideoCopyLayout.TryCreate(PixelLayout.Nv12, 4, 2, 8, 12, out var packed)
            && packed.Pitch == 4 && packed.Length == 12
            && !MediaCaptureBackend.VideoCopyLayout.TryCreate(PixelLayout.Nv12, 4, 2, -8, 24, out _),
            "NV12 packed or negative-stride validation was incorrect.");
    }

    static async Task CheckExistingRecording(string directory)
    {
        string path = Path.Combine(directory, "existing-recording-" + Guid.NewGuid().ToString("N") + ".mp4");
        byte[] sentinel = [16, 32, 64, 128];
        await File.WriteAllBytesAsync(path, sentinel);
        var recorder = new Recorder(new(path, 320, 180, 320, 180, 30, 1, 4_000_000, 0, 0));
        bool rejected = false;
        try { await recorder.Start(); }
        catch (IOException) { rejected = true; }
        finally
        {
            try { await recorder.Stop(); }
            catch (IOException) when (rejected) { }
        }
        byte[] contents = await File.ReadAllBytesAsync(path);
        Require(rejected && sentinel.AsSpan().SequenceEqual(contents),
            "A recorder opened or changed an existing destination.");
    }

    public static async Task<Dictionary<string, object>> Run(string directory)
    {
        Directory.CreateDirectory(directory); int baseline = Packet.LiveBuffers;
        var checks = new Dictionary<string, object>();
        void Mark(string name, object value) { checks[name] = value; File.WriteAllText(Path.Combine(directory, "stage.txt"), name + " : " + value); File.WriteAllText(Path.Combine(directory, "checks.json"), System.Text.Json.JsonSerializer.Serialize(checks)); }
        await Task.Run(async () =>
        {
            CheckCaptureBufferLayout();
            Mark("captureBufferBoundaries", "pass");
            using (var root = new Packet(128))
            {
                root.Bytes[0] = 91; var first = root.Retain(); var second = root.Retain();
                first.Dispose(); first.Dispose(); root.Dispose(); root.Dispose();
                Require(second.Bytes[0] == 91, "A duplicate dispose invalidated another frame consumer.");
                Parallel.For(0, 256, _ => { using var lease = second.Retain(); Require(lease.Bytes[0] == 91, "Frame data was reused while leased."); lease.Dispose(); });
                second.Dispose(); bool rejected = false; try { second.Retain(); } catch (ObjectDisposedException) { rejected = true; }
                Require(rejected, "Disposed frame was resurrected.");
            }
            Require(Packet.LiveBuffers == baseline, "Frame lease test leaked a pooled buffer.");
            Mark("independentFrameLeases", "pass");
            using (var apartment = new Apartment())
            {
                var attrs = Native.Attributes(); attrs.U32(Keys.LowLatency, 1);
                Parallel.For(0, 64, _ => attrs.Dispose());
                bool rejected = false; try { attrs.U32(Keys.LowLatency); } catch (ObjectDisposedException) { rejected = true; }
                Require(rejected, "Released COM pointer remained accessible.");
            }
            Mark("concurrentNativeRelease", "pass");
            var counter = new OperationCounter(); var outer = counter.Enter(); var inner = counter.Enter(); inner.Dispose(); inner.Dispose();
            Require(counter.IsBusy, "Nested cleanup prematurely enabled controls."); outer.Dispose(); outer.Dispose(); Require(!counter.IsBusy, "Busy counter did not recover.");
            Mark("nestedBusyScopes", "pass");
            var runtime = new RuntimeLifetime(); var workerLease = runtime.Enter();
            Require(!runtime.TryBeginShutdown(), "The media runtime shut down while a worker was active.");
            bool refused = false; try { runtime.Enter(); } catch (ObjectDisposedException) { refused = true; }
            Require(refused, "A queued media job started after global shutdown began."); workerLease.Dispose();
            var idleRuntime = new RuntimeLifetime(); Require(idleRuntime.TryBeginShutdown() && !idleRuntime.TryBeginShutdown(), "Global media shutdown was not single-owner.");
            Mark("mediaRuntimeShutdownOwnership", "pass");
            var fence = new SessionLifetime(); int old = fence.Begin(); fence.End(); int fresh = fence.Begin();
            Require(!fence.IsCurrent(old) && fence.IsCurrent(fresh), "A stale session can publish callbacks."); fence.Close();
            bool closed = false; try { fence.Begin(); } catch (ObjectDisposedException) { closed = true; } Require(closed, "A closed session restarted.");
            Mark("sessionRetirement", "pass");
            var publishFence = new SessionLifetime(); int publishingSession = publishFence.Begin();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var finishPublish = new ManualResetEventSlim();
            Task publishing = Task.Run(() => publishFence.TryUse(publishingSession, () => { entered.TrySetResult(); finishPublish.Wait(); }));
            await entered.Task; Task retiring = Task.Run(publishFence.End); await Task.Delay(25);
            Require(!retiring.IsCompleted, "Session retirement raced an accepted publication."); finishPublish.Set(); await Task.WhenAll(publishing, retiring);
            publishFence.Begin(); Require(!publishFence.TryUse(publishingSession, () => throw new InvalidOperationException("Stale publication ran")), "A retired packet entered the new session.");
            Mark("publicationAndRetirementSerialized", "pass");
            var once = new SharedOperation(); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int executions = 0;
            Task firstStop = once.Run(async () => { Interlocked.Increment(ref executions); await release.Task; });
            var stops = Enumerable.Range(0, 32).Select(_ => once.Run(() => throw new InvalidOperationException("Second cleanup executed"))).ToArray();
            Require(stops.All(t => ReferenceEquals(firstStop, t)), "Concurrent stops did not share completion."); release.SetResult(); await Task.WhenAll(stops); Require(executions == 1, "Cleanup ran twice.");
            Mark("concurrentStopCoalescing", "pass");
            int factoryCalls = 0;
            var neverStarted = new CaptureStream(() => { Interlocked.Increment(ref factoryCalls); return new TestCaptureBackend(); }, TimeSpan.FromSeconds(1));
            await Task.WhenAll(neverStarted.Stop(), neverStarted.Stop()); await ExpectCancelled(neverStarted.Start()); Require(factoryCalls == 0, "Stop-before-start opened a device.");
            Mark("captureStopBeforeStart", "pass");
            for (int i = 0; i < 64; i++)
            {
                var backend = new TestCaptureBackend(); var stream = new CaptureStream(() => backend, TimeSpan.FromSeconds(1));
                Task start = stream.Start(); Require(ReferenceEquals(start, stream.Start()), "Capture started two workers."); await start; await backend.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
                await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(stream.Stop)));
                Require(backend.DisposeCount == 1 && backend.ShutdownCount == 1, "Capture resources were released more than once.");
                await ExpectCancelled(stream.Start());
            }
            Mark("captureConcurrentStopCycles", 64);
            var delayed = new TestCaptureBackend(delayOpen: true); var duringStart = new CaptureStream(() => delayed, TimeSpan.FromSeconds(1));
            Task delayedStart = duringStart.Start(); await delayed.OpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await duringStart.Stop(); await ExpectCancelled(delayedStart); Require(delayed.DisposeCount == 1, "Cancelled startup leaked its backend.");
            Mark("captureDisconnectDuringStartup", "pass");
            var blocked = new TestCaptureBackend(blockShutdown: true); var timeout = new CaptureStream(() => blocked, TimeSpan.FromMilliseconds(100));
            await timeout.Start(); await blocked.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            bool timedOut = false; try { await timeout.Stop(); } catch (InvalidOperationException) { timedOut = true; }
            Require(timedOut && blocked.DisposeCount == 0, "Timed-out native work was released while still in use.");
            blocked.ReleaseBlockedCalls(); await timeout.Completion.WaitAsync(TimeSpan.FromSeconds(2)); Require(blocked.DisposeCount == 1, "Late native completion did not release resources.");
            Mark("unresponsiveDriverRetainsOwnership", "pass");
            var unplugged = new TestCaptureBackend(); var unplugStream = new CaptureStream(() => unplugged, TimeSpan.FromSeconds(1)); int failures = 0;
            unplugStream.Failed += _ => Interlocked.Increment(ref failures);
            await unplugStream.Start(); await unplugged.ReadEntered.Task; unplugged.Unplug(); await unplugStream.Completion.WaitAsync(TimeSpan.FromSeconds(2)); await unplugStream.Stop();
            Require(failures == 1 && unplugged.DisposeCount == 1, "Device removal did not cleanly retire the source.");
            Mark("syntheticHotUnplug", "pass");
            var observer = new TestCaptureBackend { Produce = true }; var observerStream = new CaptureStream(() => observer, TimeSpan.FromSeconds(1));
            observerStream.Data += _ => throw new InvalidOperationException("Synthetic consumer failure"); observerStream.Failed += _ => throw new InvalidOperationException("Synthetic observer failure");
            await observerStream.Start(); await observerStream.Completion.WaitAsync(TimeSpan.FromSeconds(2)); await observerStream.Stop(); Require(observer.DisposeCount == 1, "Observer failure prevented cleanup.");
            Mark("failingObserverCleanup", "pass");
        });
        for (int i = 0; i < 3; i++)
        {
            using var playback = AudioMonitor.BeginStart(48000, 2); await playback.Ready;
            using var frame = new Packet(480 * 4, true); Array.Clear(frame.Bytes, 0, frame.Length);
            Task producer = Task.Run(async () => { for (int n = 0; n < 40; n++) { playback.Submit(frame); await Task.Delay(1); } });
            await Task.Delay(20); await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => playback.Stop())); await producer; playback.Submit(frame); playback.Dispose();
        }
        Mark("audioConcurrentStopCycles", 3);
        var notStarted = new Recorder(new(Path.Combine(directory, "unused.mp4"), 320, 180, 320, 180, 30, 1, 4_000_000, 0, 0));
        await Task.WhenAll(notStarted.Stop(), notStarted.Stop()); await ExpectCancelled(notStarted.Start());
        Mark("recordingStopBeforeStart", "pass");
        await CheckExistingRecording(directory);
        Mark("existingRecordingPreserved", "pass");
        string modelFixture = Directory.CreateDirectory(Path.Combine(directory, "model-" + Guid.NewGuid().ToString("N"))).FullName;
        await File.WriteAllBytesAsync(Path.Combine(modelFixture, "nvngx_vsr.dll"), [1, 2, 3]);
        try
        {
            using var model = GpuSdk.OpenVerifiedModel(modelFixture);
            throw new InvalidOperationException("GPU model integrity accepted a modified file.");
        }
        catch (InvalidDataException) { Mark("modifiedGpuModelRejectedBeforeLoad", "pass"); }
        string recording = Path.Combine(directory, "disconnect-recording-" + Guid.NewGuid().ToString("N") + ".mp4");
        var recorder = new Recorder(new(recording, 320, 180, 320, 180, 30, 1, 4_000_000, 0, 0)); await recorder.Start();
        var recordBackend = new TestCaptureBackend { Produce = true }; var recordingStream = new CaptureStream(() => recordBackend, TimeSpan.FromSeconds(1)); recordingStream.Data += recorder.Submit;
        await recordingStream.Start(); await Task.Delay(200);
        await Task.WhenAll(recordingStream.Stop(), recorder.Stop(), recorder.Stop());
        using (var late = new Packet(320 * 180 * 4, false, 320, 180)) recorder.Submit(late);
        await recorder.Stop(); int decoded = await CountVideoSamples(recording); Require(decoded > 0 && decoded == recorder.VideoSamplesWritten, $"Finalized MP4 has {decoded} samples after {recorder.VideoSamplesWritten} video writes.");
        Mark("recordingDisconnectFinalizedFrames", decoded);
        for (int i = 0; i < 12; i++)
        {
            using var host = new Direct3DHost { Active = true };
            var window = new PreviewWindow(host) { Title = "Xeno Capture — shutdown regression", Width = 320, Height = 230, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            window.Show();
            Task producer = Task.Run(async () => { for (int n = 0; n < 30; n++) { using var packet = new Packet(64 * 64 * 4, false, 64, 64); Array.Fill(packet.Bytes, (byte)120, 0, packet.Length); host.Submit(packet); await Task.Delay(1); } });
            await Task.Delay(i % 3 == 0 ? 1 : 25); Task close = window.CloseAsync(); await Task.WhenAll(close, window.CloseAsync()).WaitAsync(TimeSpan.FromSeconds(3)); await producer;
            await Task.WhenAll(host.StopAsync(), host.StopAsync()); host.Dispose();
        }
        Mark("previewCloseWhileSubmittingCycles", 12);
        for (int i = 0; i < 4; i++)
        {
            var idle = new MainWindow(false, false, null) { ShowActivated = false }; idle.Show();
            await Task.WhenAll(idle.DiagnosticClose(), idle.DiagnosticClose()).WaitAsync(TimeSpan.FromSeconds(3));
        }
        Mark("idleWindowRepeatedCloseCycles", 4);
        var startupBackend = new TestCaptureBackend(delayOpen: true); int opened = 0;
        var starting = new MainWindow(false, false, (_, _) => { Interlocked.Increment(ref opened); return new CaptureStream(() => startupBackend, TimeSpan.FromSeconds(1)); }) { ShowActivated = false };
        starting.Show(); starting.DiagnosticPrepareInput(withAudio: true); Task startup = starting.DiagnosticConnect(); await startupBackend.OpenEntered.Task;
        await starting.DiagnosticClose().WaitAsync(TimeSpan.FromSeconds(3)); await startup.WaitAsync(TimeSpan.FromSeconds(2));
        Require(opened == 1 && !starting.DiagnosticActive && !starting.DiagnosticConnected, "Closing during startup revived an input or opened audio.");
        Mark("closeDuringStartupNoLateAudio", "pass");
        var noSignal = new TestCaptureBackend();
        var waiting = new MainWindow(false, false, (_, _) => new CaptureStream(() => noSignal, TimeSpan.FromSeconds(1))) { ShowActivated = false };
        try
        {
            waiting.Show();
            waiting.DiagnosticPrepareInput();
            await waiting.DiagnosticConnect().WaitAsync(TimeSpan.FromSeconds(4));
            Require(waiting.DiagnosticConnected && ReferenceEquals(waiting.DiagnosticPreview, waiting.DiagnosticEmbedded)
                && waiting.DiagnosticStatus.StartsWith("Waiting for HDMI video"), "A missing HDMI signal opened fullscreen or disconnected.");
            Require(!waiting.DiagnosticKeepsAwake, "A capture with no video signal prevented idle sleep.");
            await waiting.DiagnosticDisconnect().WaitAsync(TimeSpan.FromSeconds(2));
            Require(!waiting.DiagnosticConnected && noSignal.DisposeCount == 1, "No-signal input did not release.");
        }
        finally { await waiting.DiagnosticClose().WaitAsync(TimeSpan.FromSeconds(3)); }
        Mark("missingSignalStaysWindowedAndDisconnects", "pass");

        var sound = new TestCaptureBackend();
        var picture = new TestCaptureBackend { Produce = true };
        var withAudio = new MainWindow(false, false, (device, _) => new CaptureStream(() => device.Audio ? sound : picture, TimeSpan.FromSeconds(1))) { ShowActivated = false };
        try
        {
            withAudio.Show();
            withAudio.DiagnosticPrepareInput(withAudio: true);
            await withAudio.DiagnosticConnect().WaitAsync(TimeSpan.FromSeconds(3));
            await sound.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitFor(() => withAudio.DiagnosticPresented > 0);
            Require(ReferenceEquals(withAudio.DiagnosticPreview, withAudio.DiagnosticEmbedded), "Play left the embedded preview.");
            await withAudio.DiagnosticFullscreen();
            var preview = withAudio.DiagnosticPreview;
            Require(preview != null && !ReferenceEquals(preview, withAudio.DiagnosticEmbedded), "Explicit fullscreen did not open.");
            sound.Unplug();
            await WaitFor(() => withAudio.DiagnosticStatus.StartsWith("Audio input stopped."));
            long presented = withAudio.DiagnosticPresented;
            await WaitFor(() => withAudio.DiagnosticPresented > presented);
            Require(withAudio.DiagnosticConnected && ReferenceEquals(preview, withAudio.DiagnosticPreview) && sound.DisposeCount == 1,
                "An audio input failure stopped the fullscreen video.");
            await withAudio.DiagnosticWindowed();
            presented = withAudio.DiagnosticPresented;
            await WaitFor(() => withAudio.DiagnosticPresented > presented);
            Require(ReferenceEquals(withAudio.DiagnosticPreview, withAudio.DiagnosticEmbedded), "Leaving fullscreen did not restore embedded playback.");
        }
        finally { await withAudio.DiagnosticClose().WaitAsync(TimeSpan.FromSeconds(3)); }
        Mark("playStartsEmbeddedAndFullscreenIsOptional", "pass");
        Mark("audioInputFailureKeepsFullscreenVideo", "pass");

        var slowDriver = new TestCaptureBackend(blockShutdown: true) { Produce = true };
        var parallelStop = new MainWindow(false, false, (_, _) => new CaptureStream(() => slowDriver, TimeSpan.FromSeconds(3))) { ShowActivated = false };
        try
        {
            parallelStop.Show();
            parallelStop.DiagnosticPrepareInput();
            await parallelStop.DiagnosticConnectFullscreen();
            await WaitFor(() => parallelStop.DiagnosticPresented > 0);
            var teardown = Stopwatch.StartNew();
            Task stopping = parallelStop.DiagnosticDisconnect();
            await WaitFor(() => slowDriver.ShutdownCount > 0 && Direct3DHost.LiveRenderers == 0, 1500);
            Require(!stopping.IsCompleted, "Disconnect finished before the input released.");
            Mark("previewRetiredWhileInputWasStoppingMs", teardown.Elapsed.TotalMilliseconds);
            slowDriver.ReleaseBlockedCalls();
            await stopping.WaitAsync(TimeSpan.FromSeconds(2));
            Require(slowDriver.DisposeCount == 1 && !parallelStop.DiagnosticConnected, "Overlapped disconnect did not finish cleanup.");
        }
        finally
        {
            slowDriver.ReleaseBlockedCalls();
            await parallelStop.DiagnosticClose().WaitAsync(TimeSpan.FromSeconds(3));
        }

        var backends = new Queue<TestCaptureBackend>(); var firstBackend = new TestCaptureBackend { Produce = true, OneFrame = true }; var secondBackend = new TestCaptureBackend { Produce = true, OneFrame = true };
        backends.Enqueue(firstBackend); backends.Enqueue(secondBackend);
        var reconnect = new MainWindow(false, false, (_, _) => new CaptureStream(() => backends.Dequeue(), TimeSpan.FromSeconds(1))) { ShowActivated = false };
        reconnect.Show(); reconnect.DiagnosticPrepareInput(); Mark("reconnectFirstConnect", "starting"); await reconnect.DiagnosticConnect().WaitAsync(TimeSpan.FromSeconds(3)); Mark("reconnectFirstRead", reconnect.DiagnosticStatus); await firstBackend.SecondRead.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitFor(() => reconnect.DiagnosticKeepsAwake);
        Mark("videoKeepsDisplayAndSystemAwake", "pass");
        int oldSession = reconnect.DiagnosticSession;
        Mark("reconnectFirstDisconnect", "starting"); await Task.WhenAll(reconnect.DiagnosticDisconnect(), reconnect.DiagnosticDisconnect()).WaitAsync(TimeSpan.FromSeconds(3)); Mark("reconnectFirstDisconnect", "complete");
        Require(!reconnect.DiagnosticBusy && !reconnect.DiagnosticConnected && !reconnect.DiagnosticKeepsAwake,
            "Disconnect left controls busy, input attached, or a wake request active.");
        Mark("disconnectReleasesPowerRequest", "pass");
        Mark("reconnectSecondConnect", "starting"); await reconnect.DiagnosticConnect().WaitAsync(TimeSpan.FromSeconds(3)); Mark("reconnectSecondRead", reconnect.DiagnosticStatus); await secondBackend.SecondRead.Task.WaitAsync(TimeSpan.FromSeconds(3)); long freshTime = reconnect.DiagnosticFrameTime;
        await WaitFor(() => reconnect.DiagnosticKeepsAwake);
        using (var stale = new Packet(16, false, 2, 2) { Time = -12345 }) reconnect.DiagnosticStaleFrame(oldSession, stale);
        reconnect.DiagnosticStaleFailure(oldSession); await Task.Delay(50);
        Require(reconnect.DiagnosticActive && reconnect.DiagnosticConnected && reconnect.DiagnosticFrameTime == freshTime, "Old callbacks disrupted a reconnected input.");
        Mark("reconnectClose", "starting"); await reconnect.DiagnosticClose().WaitAsync(TimeSpan.FromSeconds(3)); Mark("reconnectRejectsStaleFrameAndFailure", "pass");
        Require(!reconnect.DiagnosticKeepsAwake, "Closing retained the capture power request.");
        Mark("closeAndReconnectPowerRequests", "pass");
        var recordingWindow = new MainWindow(false, false, null) { ShowActivated = false }; recordingWindow.Show(); await recordingWindow.DiagnosticDemo(); await WaitFor(() => recordingWindow.DiagnosticHasFrame);
        await recordingWindow.DiagnosticRecord(directory); await Task.Delay(200); string uiRecording = recordingWindow.DiagnosticRecordingPath ?? throw new InvalidOperationException("UI recording did not start.");
        await recordingWindow.DiagnosticClose().WaitAsync(TimeSpan.FromSeconds(5)); Require(!recordingWindow.DiagnosticRecording, "Closing left a recording active.");
        Require(await CountVideoSamples(uiRecording) > 0, "Closing did not finalize the UI recording."); Mark("closeDuringRecordingFinalizesMp4", "pass");
        var lostBackend = new TestCaptureBackend { Produce = true };
        var lost = new MainWindow(false, false, (_, _) => new CaptureStream(() => lostBackend, TimeSpan.FromSeconds(1))) { ShowActivated = false };
        lost.Show(); lost.DiagnosticPrepareInput(); await lost.DiagnosticConnect(); await WaitFor(() => lost.DiagnosticHasFrame);
        Task pendingRecord = lost.DiagnosticRecord(directory); lostBackend.Unplug(); await pendingRecord;
        await WaitFor(() => !lost.DiagnosticConnected && !lost.DiagnosticBusy);
        Require(!lost.DiagnosticRecording, "An input failure during encoder startup left a recording alive.");
        await lost.DiagnosticClose().WaitAsync(TimeSpan.FromSeconds(3)); Mark("inputFailureWhileEncoderStarting", "pass");
        var staleEncoder = new MainWindow(false, false, null) { ShowActivated = false }; staleEncoder.Show(); await staleEncoder.DiagnosticDemo(); await WaitFor(() => staleEncoder.DiagnosticHasFrame);
        await staleEncoder.DiagnosticRecord(directory); var previousEncoder = staleEncoder.DiagnosticRecorder ?? throw new InvalidOperationException("First encoder missing.");
        int recordingSession = staleEncoder.DiagnosticSession; await Task.Delay(120); await staleEncoder.DiagnosticRecord(directory);
        await staleEncoder.DiagnosticRecord(directory); staleEncoder.DiagnosticRecordingFailure(recordingSession, previousEncoder); await Task.Delay(120);
        Require(staleEncoder.DiagnosticRecording, "A retired encoder callback stopped the new recording.");
        await staleEncoder.DiagnosticClose().WaitAsync(TimeSpan.FromSeconds(5)); Mark("staleEncoderFailureDoesNotStopNewRecording", "pass");
        await WaitFor(() => Packet.LiveBuffers == baseline && MediaFoundationUsage.ActiveWorkers == 0);
        Require(CapturePowerRequest.ActiveRequests == 0, "Shutdown retained a Windows power request.");
        Mark("powerRequestsAfterShutdown", CapturePowerRequest.ActiveRequests);
        Mark("pooledBuffersAfterShutdown", Packet.LiveBuffers - baseline); Mark("mediaFoundationWorkersAfterShutdown", MediaFoundationUsage.ActiveWorkers);
        Mark("physicalUnplugTest", "not exercised - removal simulated at capture backend");
        return checks;
    }
    static Task<int> CountVideoSamples(string path) => Task.Run(() =>
    {
        using var apartment = new Apartment(); using var reader = Native.FileReader(path); int count = 0; long previous = -1;
        while (count < 5000)
        {
            using var sample = Native.Read(reader, Keys.VideoStream, out var flags, out var time);
            if ((flags & 2) != 0) break; if (sample == null) continue;
            Require(time >= previous, "Finalized recording timestamps regressed."); previous = time; count++;
        }
        return count;
    });
}








