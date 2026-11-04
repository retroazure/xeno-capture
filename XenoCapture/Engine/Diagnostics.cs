using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using XenoCapture.UI;

namespace XenoCapture.Engine;
internal static class Diagnostics
{
    public static async Task Run(string folder)
    {
        Directory.CreateDirectory(folder);
        // No physical device is opened or microphone recorded during diagnostics.
        void Stage(string step) => File.WriteAllText(Path.Combine(folder, "stage.txt"), step);
        Stage("synthetic recording");
        string path = Path.Combine(folder, "encoder-test-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture) + ".mp4");
        var result = new Dictionary<string, object> { ["runtimeVersion"] = Environment.Version.ToString(), ["captureHardware"] = "not exercised" };
        long origin = Clock.Now;
        var recorder = new Recorder(new(path, 320, 180, 640, 360, 30, 1, 4_000_000, 48000, 2), origin);
        Exception? failure = null; recorder.Failed += ex => failure = ex;
        await recorder.Start();
        try
        {
            for (int frame = 0; frame < 90; frame++)
            {
                using var v = TestSignal.Frame(frame, 320, 180); v.Time = origin + frame * 10_000_000L / 30; v.Duration = 10_000_000 / 30; recorder.Submit(v);
                using var a = TestSignal.Tone(frame * 1600, 1600); a.Time = v.Time; a.Duration = v.Duration; recorder.Submit(a);
                await Task.Delay(34);
                if (failure != null) throw failure;
            }
        }
        finally { await recorder.Stop(); }
        if (failure != null) throw failure;
        result["mp4Bytes"] = new FileInfo(path).Length;
        result["droppedFrames"] = recorder.DroppedFrames;
        result["recording"] = path;
        Stage("decode video/audio"); var verification = await Task.Run(() => Inspect(path));
        foreach (var p in verification) result[p.Key] = p.Value;
        if ((int)result["decodedVideoFrames"] < 60) throw new InvalidOperationException("Too few decoded video frames.");
        if ((int)result["decodedAudioPackets"] < 20) throw new InvalidOperationException("Audio was not encoded and decoded.");
        if ((double)result["audioRms"] < 0.015) throw new InvalidOperationException("Decoded AAC audio is silent or corrupt.");
        Stage("asynchronous source reader");
        result["asynchronousMediaFoundationRead"] = await Task.Run(() => CheckAsyncSourceReader(path));
        Stage("audio playback and UI-thread disposal");
        using (var playback = new AudioMonitor(48000, 2))
        {
            for (int i = 0; i < 12; i++)
            {
                using var silence = new Packet(480 * 4, true); Array.Clear(silence.Bytes, 0, silence.Length); playback.Submit(silence); await Task.Delay(10);
            }
            await Task.Delay(60);
            if (playback.FramesWritten == 0) throw new InvalidOperationException("WASAPI did not accept audio playback samples.");
            result["wasapiPlaybackFrames"] = playback.FramesWritten;
        }
        Stage("GPU sidecar integrity");
        var fixture = Directory.CreateDirectory(Path.Combine(folder, "sidecar-" + Guid.NewGuid().ToString("N"))).FullName;
        // Inert bytes. The integrity check must reject them before the OS loader runs.
        File.WriteAllText(Path.Combine(fixture, "XenoGpuBridge.dll"), "Xeno Capture integrity test fixture");
        try
        {
            _ = new GpuSdk.Api(fixture);
            throw new InvalidOperationException("The GPU bridge was loaded without checking its digest.");
        }
        catch (InvalidDataException error) when (error.Message.Contains("does not match the packaged GPU component", StringComparison.Ordinal))
        { result["modifiedGpuBridgeRejectedBeforeLoad"] = "pass"; }
        Stage("GPU readback"); var gpuChecks = await Task.Run(PreviewDiagnostics.Run);
        result["previewShaderBytecode"] = "pass";
        foreach (var check in gpuChecks) result[check.Key] = check.Value;
        Stage("enhanced GPU upscaling"); foreach (var check in await Task.Run(() => UpscalingDiagnostics.RunGpu(Path.Combine(folder, "upscaling")))) result[check.Key] = check.Value;
        Stage("upscaling settings and live switching"); foreach (var check in await UpscalingDiagnostics.RunUi(Path.Combine(folder, "upscaling"))) result[check.Key] = check.Value;
        Stage("RTX Video GPU path and fallback"); foreach (var check in await Task.Run(() => RtxVideoDiagnostics.RunGpu(Path.Combine(folder,"rtx-video")))) result[check.Key] = check.Value;
        Stage("RTX Video settings and lifecycle"); foreach (var check in await RtxVideoDiagnostics.RunUi(Path.Combine(folder,"rtx-video"))) result[check.Key] = check.Value;
        Stage("independent AI video preview GPU output");foreach(var check in await Task.Run(()=>AiVideoDiagnostics.RunGpu(Path.Combine(folder,"ai-video"))))result[check.Key]=check.Value;
        Stage("independent AI video preview settings and lifecycle");foreach(var check in await AiVideoDiagnostics.RunUi(Path.Combine(folder,"ai-video")))result[check.Key]=check.Value;
        Stage("frame-generation image checks"); foreach (var check in await Task.Run(() => FrameGenerationDiagnostics.RunGpu(Path.Combine(folder,"frame-generation")))) result[check.Key] = check.Value;
        Stage("frame-generation pacing and lifecycle"); foreach (var check in await FrameGenerationDiagnostics.RunUi(Path.Combine(folder,"frame-generation"))) result[check.Key] = check.Value;
        Stage("30 FPS game GPU history and cadence"); foreach(var check in await Task.Run(()=>CappedGameDiagnostics.RunGpu(Path.Combine(folder,"capped-game")))) result[check.Key]=check.Value;
        Stage("30 FPS game live pacing and lifecycle"); foreach(var check in await CappedGameDiagnostics.RunUi(Path.Combine(folder,"capped-game"))) result[check.Key]=check.Value;
        Stage("window presentation"); var presentation = await PresentationDiagnostics.Run(); foreach (var item in presentation) result[item.Key] = item.Value;
        Stage("presentation readiness regressions"); await PresentationStressDiagnostics.Run(Path.Combine(folder, "presentation-races"), false); result["presentationReadinessRaces"] = "pass";
        Stage("NV12 recording"); await CheckNv12Recording(folder, false); await CheckNv12Recording(folder, true);
        result["nativeNv12Recording"] = "pass"; result["scaledNv12Recording"] = "pass";
        Stage("shutdown regression suite"); var shutdownChecks = await LifecycleDiagnostics.Run(Path.Combine(folder, "shutdown"));
        foreach (var check in shutdownChecks) result[check.Key] = check.Value;
        Stage("multimedia scheduling ownership"); foreach (var check in SchedulingDiagnostics.CheckLifecycle()) result[check.Key] = check.Value;
        // Keep independent regression results when a high-refresh display cannot meet its target.
        result["checksBeforeHighRate"] = "PASS";
        Stage("high-rate frame generation");
        try
        {
            foreach (var check in await FrameGenerationDiagnostics.RunHighRateUi(Path.Combine(folder, "frame-generation")))
                result[check.Key] = check.Value;
            Stage("complete"); result["result"] = "PASS";
        }
        catch (Exception error)
        {
            result["result"] = "FAIL"; result["highRateFailure"] = error.Message;
            throw;
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "results.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    static string CheckAsyncSourceReader(string path)
    {
        using var apartment = new Apartment();
        using var cancel = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var callback = new SourceReaderCallback(cancel.Token);
        using var attributes = Native.Attributes();
        callback.Attach(attributes);
        using var reader = Native.FileReader(path, attributes);
        Native.Select(reader, Keys.AllStreams, false);
        Native.Select(reader, Keys.VideoStream, true);
        int frames = 0;
        long previous = -1;
        try
        {
            while (frames < 8)
            {
                using var sample = callback.Read(reader, Keys.VideoStream, out var flags, out var timestamp);
                if ((flags & 3) != 0) throw new InvalidOperationException("The asynchronous reader ended early.");
                if (sample == null) continue;
                if (timestamp < previous) throw new InvalidOperationException("The asynchronous reader returned out-of-order samples.");
                previous = timestamp;
                frames++;
            }
            cancel.Cancel();
            try { using var unexpected = callback.Read(reader, Keys.VideoStream, out _, out _); }
            catch (OperationCanceledException) { return "pass"; }
            throw new InvalidOperationException("The cancelled reader accepted another sample request.");
        }
        finally { callback.FlushPending(reader, Keys.VideoStream); }
    }

    static async Task CheckNv12Recording(string folder, bool scale)
    {
        string variant = scale ? "scaled" : "native";
        var path = Path.Combine(folder, $"nv12-{variant}-{Guid.NewGuid():N}.mp4");
        long origin = Clock.Now;
        var recorder = new Recorder(new(path, 320, 180, scale ? 640 : 320, scale ? 360 : 180, 30, 1, 4_000_000, 0, 0, PixelLayout.Nv12), origin);
        await recorder.Start();
        try
        {
            for (int i = 0; i < 12; i++)
            {
                using var frame = new Packet(320 * 180 * 3 / 2, false, 320, 180, PixelLayout.Nv12) { Time = origin + i * 333333L, Duration = 333333 };
                Array.Fill(frame.Bytes, (byte)128, 0, frame.Length); recorder.Submit(frame); await Task.Delay(34);
            }
        }
        finally { await recorder.Stop(); }
        await Task.Run(() =>
        {
            using var apartment = new Apartment(); using var reader = Native.FileReader(path);
            using var type = Native.MediaType(reader, Keys.VideoStream, 0, true) ?? throw new InvalidOperationException("Missing NV12 recording stream.");
            var (w, h) = Keys.Unpair(type.U64(Keys.Size));
            if (w != (scale ? 640 : 320) || h != (scale ? 360 : 180)) throw new InvalidOperationException("Incorrect NV12 recording size.");
            int count = 0;
            while (count < 30) { using var sample = Native.Read(reader, Keys.VideoStream, out var flags, out _); if ((flags & 2) != 0) break; if (sample != null) count++; }
            if (count != 12) throw new InvalidOperationException("NV12 recording lost video samples.");
        });
    }
    static unsafe Dictionary<string, object> Inspect(string path)
    {
        using var apartment = new Apartment();
        using var reader = Native.FileReader(path);
        using var vt = Native.MediaType(reader, Keys.VideoStream, 0, true) ?? throw new InvalidOperationException("Missing video stream.");
        var (w, h) = Keys.Unpair(vt.U64(Keys.Size));
        if (w != 640 || h != 360) throw new InvalidOperationException("The recorded video was not upscaled.");
        using var at = Native.MediaType(reader, Keys.AudioStream, 0, true) ?? throw new InvalidOperationException("Missing audio stream.");
        if (at.U32(Keys.SampleRate) != 48000 || at.U32(Keys.Channels) != 2) throw new InvalidOperationException("Incorrect audio stream format.");
        int videos = 0, audios = 0; long previous = -1, finalVideoTime = 0;
        Native.Select(reader, Keys.AllStreams, false); Native.Select(reader, Keys.VideoStream, true);
        for (int i = 0; i < 1000; i++)
        {
            using var sample = Native.Read(reader, Keys.VideoStream, out var flags, out var timestamp);
            if ((flags & 2) != 0) break;
            if (sample == null) continue;
            if (timestamp < previous) throw new InvalidOperationException("Video timestamps are not monotonic.");
            previous = timestamp; finalVideoTime = timestamp; videos++;
        }
        using var ar = Native.FileReader(path); Native.Select(ar, Keys.AllStreams, false); Native.Select(ar, Keys.AudioStream, true);
        using var pcm = Native.Type(); pcm.Guid(Keys.Major, Keys.Audio); pcm.Guid(Keys.Subtype, Keys.Pcm); pcm.U32(Keys.Bits, 16); Native.SetType(ar, Keys.AudioStream, pcm);
        double squared = 0; long samples = 0, audioEnd = 0;
        for (int i = 0; i < 1000; i++)
        {
            using var sample = Native.Read(ar, Keys.AudioStream, out var flags, out var timestamp); if ((flags & 2) != 0) break; if (sample == null) continue;
            nint bp; Native.Hr(((delegate* unmanaged[Stdcall]<nint, nint*, int>)sample.Slot(41))(sample.P, &bp)); using var buffer = new Com(bp);
            nint data; uint max, size; Native.Hr(((delegate* unmanaged[Stdcall]<nint, nint*, uint*, uint*, int>)buffer.Slot(3))(bp, &data, &max, &size));
            try { var span = new ReadOnlySpan<short>((void*)data, (int)size / 2); foreach (short s in span) { double f = s / 32768.0; squared += f * f; samples++; } }
            finally { Native.Hr(buffer.Call(4)); }
            audios++; audioEnd = timestamp;
        }
        if (finalVideoTime < 28_000_000 || Math.Abs(finalVideoTime - audioEnd) > 1_000_000) throw new InvalidOperationException("Recorded duration or A/V alignment is incorrect.");
        return new() { ["decodedVideoFrames"] = videos, ["decodedAudioPackets"] = audios, ["resolution"] = $"{w}x{h}", ["audioRms"] = Math.Sqrt(squared / Math.Max(1, samples)), ["lastVideoTime100ns"] = finalVideoTime, ["lastAudioTime100ns"] = audioEnd, ["bicubicResize"] = "pass", ["aacRoundTrip"] = "pass" };
    }
}





