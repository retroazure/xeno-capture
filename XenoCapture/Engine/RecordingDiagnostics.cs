using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using XenoCapture.UI;
using Screen = System.Windows.Forms.Screen;
using System.Threading;
using System.Threading.Tasks;

namespace XenoCapture.Engine;

internal static class RecordingDiagnostics
{
    internal static async Task<object> Run(string folder)
    {
        var captures = new List<object>();
        foreach (int fps in new[] { 30, 60, 120 })
            captures.Add(await RecordThroughUi(folder, fps));

        var highDefinition = new List<object>();
        foreach (bool scale in new[] { false, true })
            highDefinition.Add(await MeasureHighDefinition(folder, scale));
        CheckResizePixels();
        return new { result = "PASS", captures, highDefinition, resizeColorAndAspect = "PASS" };
    }

    internal static async Task<object> RunFullscreen(string folder)
    {
        var displays = new List<object>();
        foreach (var display in Screen.AllScreens.OrderBy(item => item.Bounds.Height))
        {
            var measurements = new List<object>();
            // Alternate which implementation runs first to limit warmup and ordering effects.
            for (int trial = 0; trial < 2; trial++)
                foreach (bool useCpu in new[] { trial == 0, trial != 0 })
                    measurements.Add(await MeasureFullscreen(folder, display, useCpu));
            displays.Add(new { display.Bounds.Width, display.Bounds.Height, measurements });
        }
        if (displays.Count == 0) throw new InvalidOperationException("No display was available for the fullscreen test.");
        return new { result = "PASS", displays };
    }

    static async Task<object> MeasureFullscreen(string folder, Screen display, bool useCpu)
    {
        var source = new PresentationStressDiagnostics.PacedVideoBackend(1920, 1080, 30_000, pan: true);
        var main = new MainWindow(false, false, (_, _) => new CaptureStream(() => source, TimeSpan.FromSeconds(2)))
            { ShowActivated = false };
        main.Show();
        try
        {
            main.DiagnosticPrepareInput();
            main.DiagnosticRecordingSettings(2, 3, useCpu); // 1440p at 120 Mbps.
            main.DiagnosticSelectFrameGeneration(FrameGenerationMode.RecoverGaps);
            main.DiagnosticSetSteadyFullscreenPacing(true);
            await main.DiagnosticConnect();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            while (!main.DiagnosticHasFrame) await Task.Delay(10, timeout.Token);
            main.DiagnosticSelectAiVideo(true, 2160, 4);
            await main.DiagnosticFullscreenOnDisplay(display.Bounds);
            var host = main.DiagnosticPreview ?? throw new InvalidOperationException("No fullscreen preview host.");
            while (!host.AiVideoActive)
            {
                if (host.AiVideoFallbackReason is { } reason) throw new InvalidOperationException(reason);
                if (main.DiagnosticPreviewError is { } failure) throw failure;
                await Task.Delay(10, timeout.Token);
            }
            await Task.Delay(350);
            await main.DiagnosticRecord(folder);
            var recorder = main.DiagnosticRecorder ?? throw new InvalidOperationException(main.DiagnosticStatus);
            string path = main.DiagnosticRecordingPath ?? throw new InvalidOperationException("Recording path is missing.");
            var frames = new List<(long At, long Time)>();
            void Presented(PresentedFrame frame)
            {
                if (frame.Kind == PresentationKind.Source)
                    lock (frames) frames.Add((Clock.Now, frame.Time));
            }
            host.FramePresented += Presented;
            try { await Task.Delay(6000); }
            finally { host.FramePresented -= Presented; }
            if (!host.AiVideoActive) throw new InvalidOperationException("AI stopped during fullscreen recording.");
            var fullscreen = SummarizeFullscreen(host, frames);
            await main.DiagnosticRecord(folder);
            await main.DiagnosticWindowed();
            var output = await Task.Run(() => Inspect(path));
            if (output.Width != 2560 || output.Height != 1440 || Math.Abs(output.SampleFps - 60) > .1)
                throw new InvalidOperationException("The fullscreen recording changed the output resolution or cadence.");
            return new { useCpu, fullscreen, output, droppedFrames = recorder.DroppedFrames,
                writtenFrames = recorder.VideoSamplesWritten };
        }
        finally { await main.DiagnosticClose().WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    static async Task<object> RecordThroughUi(string folder, int fps)
    {
        var source = new FrameGenerationTestSource(fps, width: 320, height: 180);
        var main = new MainWindow(false, false, (_, _) => new CaptureStream(() => source, TimeSpan.FromSeconds(2)))
            { ShowActivated = false };
        main.Show();
        try
        {
            // The synthetic selector advertises 30 FPS. Drive the recorder
            // from the actual video samples supplied by the backend.
            main.DiagnosticPrepareInput();
            await main.DiagnosticConnect();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            while (!main.DiagnosticHasFrame) await Task.Delay(10, timeout.Token);
            await main.DiagnosticRecord(folder);
            var recorder = main.DiagnosticRecorder ?? throw new InvalidOperationException(main.DiagnosticStatus);
            string path = main.DiagnosticRecordingPath ?? throw new InvalidOperationException("Recording path is missing.");
            await Task.Delay(1400);
            await main.DiagnosticRecord(folder);
            var output = await Task.Run(() => Inspect(path));
            if (Math.Abs(output.SampleFps - fps) > .1
                || Math.Abs((double)output.Numerator / output.Denominator - fps) > .1
                || recorder.VideoSamplesWritten != output.Samples || recorder.DroppedFrames != 0)
                throw new InvalidOperationException($"The {fps} FPS capture was not recorded at the source frame rate.");
            return new { sourceFps = fps, output, droppedFrames = recorder.DroppedFrames };
        }
        finally { await main.DiagnosticClose().WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    static readonly VideoFrameRate TestRate = new(60_000, 1001);

    static async Task<object> MeasureHighDefinition(string folder, bool scale)
    {
        int width = scale ? 2560 : 1920, height = scale ? 1440 : 1080;
        const int count = 180;
        string variant = scale ? "1440p" : "1080p";
        string path = Path.Combine(folder, $"recording-{variant}-{Guid.NewGuid():N}.mp4");
        var recorder = new Recorder(new(path, 1920, 1080, width, height, TestRate.Numerator, TestRate.Denominator,
            120_000_000, 0, 0, PixelLayout.Nv12));
        try
        {
            await recorder.Start();
            await Task.Run(() => Play(count, recorder));
        }
        finally { await recorder.Stop(); }

        var output = await Task.Run(() => Inspect(path));
        if (output.Width != width || output.Height != height
            || output.Numerator != TestRate.Numerator || output.Denominator != TestRate.Denominator
            || Math.Abs(output.SampleFps - TestRate.FramesPerSecond) > .1)
            throw new InvalidOperationException("High-definition recording changed the resolution or source cadence.");
        if (recorder.DroppedFrames != 0 || output.Samples != count)
            throw new InvalidOperationException("High-definition recording lost source samples.");
        return new { variant, submittedFrames = count, output, droppedFrames = recorder.DroppedFrames,
            writtenFrames = recorder.VideoSamplesWritten };
    }

    static void Play(int count, Recorder recorder)
    {
        const int width = 1920, height = 1080, lumaLength = width * height;
        using var signal = new AutoResetEvent(false);
        using var clock = new FrameDeadlineWaiter(signal);
        long start = Clock.Now + 50_000;
        long Time(int index) => start + index * 10_000_000L * TestRate.Denominator / TestRate.Numerator;
        for (int i = 0; i < count; i++)
        {
            long time = Time(i);
            using var packet = new Packet(lumaLength * 3 / 2, false, width, height, PixelLayout.Nv12)
            {
                Time = time, Duration = Time(i + 1) - time, VideoRate = TestRate
            };
            Array.Fill(packet.Bytes, (byte)(32 + i % 45), 0, lumaLength);
            Array.Fill(packet.Bytes, (byte)128, lumaLength, lumaLength / 2);
            // A moving edge gives the renderer and encoder changing image detail.
            int left = (8 * i) % (width - 128);
            for (int y = 160; y < 920; y++)
                packet.Bytes.AsSpan(y * width + left, 128).Fill(214);
            clock.WaitUntil(time);
            recorder.Submit(packet);
        }
    }

    static object SummarizeFullscreen(Direct3DHost host, List<(long At, long Time)> frames)
    {
        (long At, long Time)[] sample;
        lock (frames) sample = frames.ToArray();
        if (sample.Length < 2) throw new InvalidOperationException("Fullscreen did not present enough source frames.");
        var gaps = sample.Zip(sample.Skip(1), (a, b) => (b.At - a.At) / 10000.0).Order().ToArray();
        var ages = sample.Select(frame => (frame.At - frame.Time) / 10000.0).Order().ToArray();
        double Value(double[] values, double percentile) => values[(int)((values.Length - 1) * percentile)];
        return new
        {
            refreshHz = DisplayTiming.RefreshRate(host.Handle),
            aiVideo = host.AiVideoStatus,
            sourcePresentations = sample.Length,
            gapP95Ms = Value(gaps, .95),
            gapP99Ms = Value(gaps, .99),
            maxGapMs = gaps[^1],
            gapsOver25Ms = gaps.Count(gap => gap > 25),
            sampleAgeP95Ms = Value(ages, .95),
            frameWaitTimeouts = host.FrameWaitTimeouts
        };
    }

    static void CheckResizePixels()
    {
        const int width = 160, height = 120, lumaSize = width * height;
        using var source = new Packet(lumaSize * 3 / 2, false, width, height, PixelLayout.Nv12) { Bt601 = true };
        Array.Fill(source.Bytes, (byte)126, 0, lumaSize);
        for (int i = lumaSize; i < source.Length; i += 2)
        {
            source.Bytes[i] = 105;
            source.Bytes[i + 1] = 163;
        }
        using var expected = PixelConversion.ToBgra(source);
        using var scaler = new D3DPreview.RecordingScaler(320, 180);
        var pixels = scaler.Resize(source);
        for (int channel = 0; channel < 3; channel++)
        {
            int center = (90 * 320 + 160) * 4 + channel;
            if (pixels[channel] != 0 || pixels[(90 * 320 + 10) * 4 + channel] != 0
                || pixels[(90 * 320 + 310) * 4 + channel] != 0
                || Math.Abs(pixels[center] - expected.Bytes[channel]) > 2)
                throw new InvalidOperationException("The recording scaler altered NV12 color or aspect ratio.");
        }
    }

    sealed record Mp4Video(int Width, int Height, int Numerator, int Denominator, int Samples, double SampleFps);

    static Mp4Video Inspect(string path)
    {
        using var apartment = new Apartment();
        using var reader = Native.FileReader(path);
        using var type = Native.MediaType(reader, Keys.VideoStream, 0, true)
            ?? throw new InvalidOperationException("The MP4 has no video stream.");
        var (width, height) = Keys.Unpair(type.U64(Keys.Size));
        var (numerator, denominator) = Keys.Unpair(type.U64(Keys.Rate));
        int samples = 0;
        long first = -1, last = -1;
        for (int i = 0; i < 1000; i++)
        {
            using var sample = Native.Read(reader, Keys.VideoStream, out var flags, out long timestamp);
            if ((flags & 2) != 0) break;
            if (sample == null) continue;
            if (timestamp <= last) throw new InvalidOperationException("The MP4 has non-increasing video timestamps.");
            if (first < 0) first = timestamp;
            last = timestamp;
            samples++;
        }
        if (samples < 2 || denominator <= 0) throw new InvalidOperationException("The MP4 has insufficient timing information.");
        double sampleFps = (samples - 1) * 10_000_000.0 / (last - first);
        return new(width, height, numerator, denominator, samples, sampleFps);
    }
}
