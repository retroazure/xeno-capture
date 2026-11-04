using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace XenoCapture.Engine;
internal static class UpscalingDiagnostics
{
    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    public static async Task Run(string folder)
    {
        Directory.CreateDirectory(folder);
        var checks = await Task.Run(() => RunGpu(folder));
        foreach (var check in await RunUi(folder)) checks[check.Key] = check.Value;
        checks["result"] = "PASS";
        await File.WriteAllTextAsync(Path.Combine(folder, "results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
    }
    internal static Dictionary<string, object> RunGpu(string folder)
    {
        Directory.CreateDirectory(folder); using var apartment = new Apartment();
        var checks = new Dictionary<string, object>();
        void Mark(string text) => File.WriteAllText(Path.Combine(folder, "stage.txt"), text);
        using var scene = Scene();
        foreach (int height in new[] { 1440, 2160 })
        {
            Mark("1080p to " + height); int width = height * 16 / 9;
            using var renderer = new D3DPreview(0, width, height);
            renderer.Draw(scene, .25); byte[] standard = renderer.Readback();
            renderer.Draw(scene, .25, PreviewUpscaler.Enhanced); byte[] enhanced = renderer.Readback();
            Require(renderer.UsedEnhancedUpscaling, renderer.UpscalingFallbackReason ?? "Enhanced upscaling did not activate.");
            Require(renderer.UpscalingPasses == 1, "1080p to 1440p/4K should use one NIS stage.");
            Require(!standard.AsSpan().SequenceEqual(enhanced), "The enhanced filter did not change detailed pixels.");
            SavePng(Path.Combine(folder, $"standard-{height}.png"), standard, width, height);
            SavePng(Path.Combine(folder, $"enhanced-{height}.png"), enhanced, width, height);
            renderer.Draw(scene, .25); Require(standard.AsSpan().SequenceEqual(renderer.Readback()), "Turning enhanced upscaling off changed Standard preview pixels.");
            for (int i = 0; i < 8; i++) renderer.Draw(scene, .25, PreviewUpscaler.Enhanced); Require(enhanced.AsSpan().SequenceEqual(renderer.Readback()), "Repeated identical input produced unstable enhanced pixels.");
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < 120; i++) renderer.Draw(scene, .25, PreviewUpscaler.Enhanced);
            byte[] steady = renderer.Readback(); Require(enhanced.AsSpan().SequenceEqual(steady), "Enhanced pixels changed during the rendering batch."); checks[$"rgb1080To{height}GpuBatchMeanMs"] = Stopwatch.GetElapsedTime(start).TotalMilliseconds / 120;
            checks[$"enhanced{height}Sha256"] = Convert.ToHexString(SHA256.HashData(enhanced));
            if (height == 2160)
            {
                SaveCrop(Path.Combine(folder, "standard-detail.png"), standard, width, 120, 120, 1320, 420);
                SaveCrop(Path.Combine(folder, "enhanced-detail.png"), enhanced, width, 120, 120, 1320, 420);
                renderer.Draw(scene, 0, PreviewUpscaler.Enhanced); byte[] soft = renderer.Readback();
                renderer.Draw(scene, 1, PreviewUpscaler.Enhanced); Require(!soft.AsSpan().SequenceEqual(renderer.Readback()), "NIS sharpening did not respond to its slider.");
            }
        }
        Mark("repeated 1080p captures with Normal upscaling");
        checks["normal4kRepeatedCapture"] = CheckRepeatedCapture(scene);
        Mark("fullscreen edge smoothing");
        using (var edge = new Packet(64 * 64 * 4, false, 64, 64))
        {
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                {
                    int i = (y * 64 + x) * 4;
                    byte value = x + .5 > 14.2 + .47 * (y + .5) ? (byte)255 : (byte)0;
                    edge.Bytes[i] = edge.Bytes[i + 1] = edge.Bytes[i + 2] = value;
                    edge.Bytes[i + 3] = 255;
                }

            foreach (int size in new[] { 64, 128 })
            {
                using var renderer = new D3DPreview(0, size, size);
                renderer.Draw(edge, 0);
                byte[] standard = renderer.Readback();
                renderer.EdgeSmoothing = true;
                renderer.Draw(edge, 0);
                byte[] smoothed = renderer.Readback();
                double standardError = EdgeError(standard, size), smoothedError = EdgeError(smoothed, size);
                Require(smoothedError < standardError * .95, "Fullscreen smoothing did not improve the diagonal against a supersampled reference.");
                int black = (size / 2 * size + 2) * 4, white = (size / 2 * size + size - 3) * 4;
                Require(smoothed[black] == 0 && smoothed[white] == 255, "Fullscreen smoothing changed a flat region.");
                checks[$"edgeSmoothing{size}ErrorRatio"] = smoothedError / standardError;
                if (size > edge.Width)
                {
                    renderer.Draw(edge, 0, PreviewUpscaler.Enhanced);
                    byte[] enhancedSmoothed = renderer.Readback();
                    renderer.EdgeSmoothing = false;
                    renderer.Draw(edge, 0, PreviewUpscaler.Enhanced);
                    byte[] enhanced = renderer.Readback();
                    Require(renderer.UsedEnhancedUpscaling && !enhancedSmoothed.AsSpan().SequenceEqual(enhanced),
                        "Fullscreen smoothing did not apply to the NIS output.");
                    Require(enhancedSmoothed[black] == enhanced[black] && enhancedSmoothed[white] == enhanced[white],
                        "Fullscreen smoothing changed a flat region of the NIS output.");
                    checks["edgeSmoothingWithNis"] = "pass";
                }
            }
        }
        Mark("NV12 color and range");
        using (var renderer = new D3DPreview(0, 640, 480))
        using (var frame = new Packet(320 * 240 * 3 / 2, false, 320, 240, PixelLayout.Nv12))
        {
            Array.Fill(frame.Bytes, (byte)100, 0, 320 * 240);
            for (int i = 320 * 240; i < frame.Length; i += 2) { frame.Bytes[i] = 90; frame.Bytes[i + 1] = 155; }
            foreach (bool full in new[] { false, true }) foreach (bool bt601 in new[] { false, true })
            {
                frame.FullRange = full; frame.Bt601 = bt601;
                renderer.Draw(frame, .25, PreviewUpscaler.Enhanced); Require(renderer.UsedEnhancedUpscaling, "NV12 NIS did not activate.");
                byte[] image = renderer.Readback(); using var expected = PixelConversion.ToBgra(frame);
                foreach (int offset in new[] { (240 * 640 + 320) * 4, (10 * 640 + 10) * 4, (470 * 640 + 630) * 4 })
                    for (int c = 0; c < 3; c++) Require(Math.Abs(image[offset + c] - expected.Bytes[c]) <= 2, "NIS altered NV12 color/range or channel order.");
            }
        }
        checks["nv12FullLimitedRangeAndMatrices"] = "pass";
        Mark("odd sizes, scaling chains and borders");
        using (var frame = new Packet(64 * 36 * 4, false, 64, 36))
        using (var renderer = new D3DPreview(0, 641, 361))
        {
            for (int i = 0; i < frame.Length; i += 4) { frame.Bytes[i] = 22; frame.Bytes[i + 1] = 44; frame.Bytes[i + 2] = 175; frame.Bytes[i + 3] = 255; }
            renderer.Draw(frame, .25, PreviewUpscaler.Enhanced); byte[] image = renderer.Readback();
            Require(renderer.UsedEnhancedUpscaling && renderer.UpscalingPasses == 4, "Large-ratio upscaling did not chain bounded NIS stages.");
            foreach (int offset in new[] { (1 * 641 + 1) * 4, (180 * 641 + 320) * 4, (359 * 641 + 639) * 4 })
                for (int c = 0; c < 3; c++) Require(Math.Abs(image[offset + c] - frame.Bytes[c]) <= 2, "Odd-size NIS output has incomplete borders or altered flat colors.");
            renderer.Resize(640, 480); renderer.Draw(frame, .25, PreviewUpscaler.Enhanced); image = renderer.Readback();
            Require(image[0] == 0 && image[1] == 0 && image[2] == 0, "Enhanced upscaling did not preserve letterboxing.");
            renderer.Resize(64, 36); renderer.Draw(frame, .25, PreviewUpscaler.Enhanced); Require(!renderer.UsedEnhancedUpscaling, "NIS should not resample an input already at display size.");
        }
        checks["oddDimensionsBoundedStagesAndLetterbox"] = "pass";
        using (var renderer = new D3DPreview(0, 640, 360) { ForceUpscalingUnavailable = true })
        using (var small = TestSignal.Frame(0, 320, 180))
        {
            renderer.Draw(small, .25); byte[] standard = renderer.Readback();
            renderer.Draw(small, .25, PreviewUpscaler.Enhanced);
            Require(!renderer.UsedEnhancedUpscaling && renderer.UpscalingFallbackReason != null && standard.AsSpan().SequenceEqual(renderer.Readback()), "Unavailable upscaling failed to preserve Standard preview.");
        }
        foreach (int error in new[] { unchecked((int)0x80070057), unchecked((int)0x8007000E), unchecked((int)0x887A0004) }) Require(D3DPreview.CanFallbackFromUpscaling(Marshal.GetExceptionForHR(error)!), "A native unsupported/resource error would bypass the safe preview fallback.");
        Require(!D3DPreview.CanFallbackFromUpscaling(new COMException("Device removed", unchecked((int)0x887A0005))), "A device-loss error would be hidden by the upscaling fallback.");
        checks["unsupportedDeviceFallback"] = "pass"; checks["deviceLossStillPropagates"] = "pass";
        checks["standardPixelsAfterToggle"] = "identical"; checks["staticInputTemporalStability"] = "pass"; checks["enhancedSharpnessControl"] = "pass";
        checks["captureHardware"] = "not exercised"; checks["measuresMonitorPresentation"] = false;
        Mark("GPU checks complete"); return checks;
    }
    static object CheckRepeatedCapture(Packet scene)
    {
        using var other = Copy(scene);
        other.Bytes.AsSpan(0, 4096).Fill(72);
        using var repeatedScene = Copy(scene);
        using var repeatedOther = Copy(other);
        Packet[] input = [scene, repeatedScene, other, repeatedOther];
        using var renderer = new D3DPreview(0, 3840, 2160);
        const int frames = 48;
        (double Ms, long Evaluations, byte[] Pixels) Run(bool disableCache)
        {
            renderer.DiagnosticDisableProcessedCache = disableCache;
            for (int i = 0; i < 8; i++) renderer.Draw(input[i % 4], .25, PreviewUpscaler.Enhanced);
            renderer.Readback();
            long initialEvaluations = renderer.NisEvaluations;
            long started = Stopwatch.GetTimestamp();
            for (int i = 0; i < frames; i++) renderer.Draw(input[i % 4], .25, PreviewUpscaler.Enhanced);
            byte[] pixels = renderer.Readback();
            return (Stopwatch.GetElapsedTime(started).TotalMilliseconds / frames, renderer.NisEvaluations - initialEvaluations, pixels);
        }

        var baseline = Run(disableCache: true);
        var cached = Run(disableCache: false);
        Require(baseline.Evaluations == frames && cached.Evaluations == frames / 2
            && baseline.Pixels.AsSpan().SequenceEqual(cached.Pixels), "NIS repeat caching did not halve GPU work with identical output.");
        return new { frames, baseline.Ms, CachedMs = cached.Ms, baseline.Evaluations, CachedEvaluations = cached.Evaluations };

        static Packet Copy(Packet source)
        {
            var copy = new Packet(source.Length, false, source.Width, source.Height, source.Layout)
                { FullRange = source.FullRange, Bt601 = source.Bt601 };
            source.Bytes.AsSpan(0, source.Length).CopyTo(copy.Bytes);
            return copy;
        }
    }

    static double EdgeError(byte[] pixels, int size)
    {
        const int samples = 8;
        double scale = 64.0 / size, error = 0;
        for (int y = size / 6; y < size * 5 / 6; y++)
            for (int x = 2; x < size - 2; x++)
            {
                if (Math.Abs((x + .5) * scale - 14.2 - .47 * (y + .5) * scale) > 2) continue;
                int coverage = 0;
                for (int sy = 0; sy < samples; sy++)
                    for (int sx = 0; sx < samples; sx++)
                        if ((x + (sx + .5) / samples) * scale > 14.2 + .47 * (y + (sy + .5) / samples) * scale)
                            coverage++;
                double delta = pixels[(y * size + x) * 4] / 255.0 - coverage / (double)(samples * samples);
                error += delta * delta;
            }
        return error;
    }

    internal static async Task<Dictionary<string, object>> RunUi(string folder)
    {
        var main = new MainWindow(false, false, (_, _) => new CaptureStream(() => new TestCaptureBackend { Produce = true }, TimeSpan.FromSeconds(2))) { ShowActivated = false };
        async Task Wait(Func<bool> condition)
        {
            var timer = Stopwatch.StartNew();
            while (!condition()) { if (timer.ElapsedMilliseconds > 5000) throw new TimeoutException("Upscaling UI check did not become ready."); await Task.Delay(10); }
        }
        main.Show();
        try
        {
            string legacy = JsonSerializer.Serialize(new { Folder = folder, Resolution = 0, Bitrate = 1, Sharpness = .25 });
            main.DiagnosticApplySettingsJson(legacy);
            Require(main.DiagnosticUpscaler == PreviewUpscaler.Standard && main.DiagnosticEmbedded.Upscaler == PreviewUpscaler.Standard, "Older settings did not retain Standard preview.");
            main.DiagnosticSelectUpscaler(PreviewUpscaler.Enhanced); string saved = main.DiagnosticSettingsJson;
            using (var json = JsonDocument.Parse(saved)) Require(json.RootElement.GetProperty("Upscaling").GetInt32() == 1, "Enhanced preference was not serialized.");
            main.DiagnosticSelectUpscaler(PreviewUpscaler.Standard); main.DiagnosticApplySettingsJson(saved);
            Require(main.DiagnosticUpscaler == PreviewUpscaler.Enhanced && main.DiagnosticEmbedded.Upscaler == PreviewUpscaler.Enhanced, "Saved enhanced preference did not restore to the preview host.");
            main.DiagnosticPrepareInput(); await main.DiagnosticConnectFullscreen(maximize: false);
            await Wait(() => main.DiagnosticPreview?.EnhancedUpscalingActive == true);
            Require(main.DiagnosticPreview!.Upscaler == PreviewUpscaler.Enhanced, "New fullscreen host ignored the upscaling preference.");
            main.DiagnosticSelectUpscaler(PreviewUpscaler.Standard);
            await Wait(() => main.DiagnosticPreview?.EnhancedUpscalingActive == false);
            Require(main.DiagnosticEmbedded.Upscaler == PreviewUpscaler.Standard, "Changing fullscreen upscaling did not update the retained windowed host.");
            await main.DiagnosticWindowed(); main.DiagnosticSelectUpscaler(PreviewUpscaler.Enhanced);
            await Wait(() => main.DiagnosticPreview?.EnhancedUpscalingActive == true);
            for (int cycle = 0; cycle < 3; cycle++)
            {
                await main.DiagnosticFullscreen(); await Wait(() => main.DiagnosticPreview?.EnhancedUpscalingActive == true);
                await main.DiagnosticWindowed(); await Wait(() => main.DiagnosticPreview?.EnhancedUpscalingActive == true);
            }
            await Dispatcher.Yield(DispatcherPriority.Render); main.UpdateLayout();
            var screenshot = new RenderTargetBitmap((int)Math.Ceiling(main.ActualWidth), (int)Math.Ceiling(main.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            screenshot.Render(main);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(screenshot));
            using (var stream = File.Create(Path.Combine(folder, "interface.png"))) encoder.Save(stream);
            await main.DiagnosticDisconnect(); Require(!main.DiagnosticConnected && !main.DiagnosticBusy, "Disconnect failed while enhanced upscaling was active.");
            return new() { ["legacySettingsDefaultToStandard"] = "pass", ["enhancedSettingsRoundTrip"] = "pass", ["liveUpscalingModeSwitch"] = "pass", ["fullscreenUpscalingRoundTrips"] = 3, ["disconnectWhileUpscaling"] = "pass" };
        }
        finally { await main.DiagnosticClose().WaitAsync(TimeSpan.FromSeconds(5)); }
    }
    internal static void SavePng(string path, byte[] pixels, int width, int height)
    {
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream);
    }
    static void SaveCrop(string path, byte[] pixels, int strideWidth, int x, int y, int width, int height)
    {
        var crop = new byte[width * height * 4];
        for (int row = 0; row < height; row++) Array.Copy(pixels, ((y + row) * strideWidth + x) * 4, crop, row * width * 4, width * 4);
        SavePng(path, crop, width, height);
    }
    static unsafe Packet Scene()
    {
        const int width = 1920, height = 1080; var frame = new Packet(width * height * 4, false, width, height);
        fixed (byte* data = frame.Bytes)
        {
            using var bitmap = new Drawing.Bitmap(width, height, width * 4, Drawing.Imaging.PixelFormat.Format32bppArgb, (nint)data);
            using var graphics = Drawing.Graphics.FromImage(bitmap); graphics.Clear(Drawing.Color.FromArgb(20, 29, 44));
            graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var green = new Drawing.SolidBrush(Drawing.Color.FromArgb(186, 242, 121));
            using var white = new Drawing.SolidBrush(Drawing.Color.FromArgb(230, 237, 245));
            using var font = new Drawing.Font("Segoe UI", 28, Drawing.FontStyle.Bold);
            using var small = new Drawing.Font("Segoe UI", 15);
            graphics.DrawString("XENO / IMAGE UPSCALING", font, white, 72, 72);
            graphics.DrawString("1080p input  |  Edges, text and fine detail", small, green, 74, 128);
            using var grid = new Drawing.Pen(Drawing.Color.FromArgb(60, 86, 108), 1);
            using var contour = new Drawing.Pen(Drawing.Color.FromArgb(186, 242, 121), 2);
            for (int x = 0; x < width; x += 28) graphics.DrawLine(grid, 960, 340, x, 1080);
            for (int y = 420; y < height; y += 33) graphics.DrawLine(grid, 0, y, width, y);
            for (int i = 0; i < 8; i++) graphics.DrawEllipse(contour, 220 + i * 21, 280 + i * 13, 380 - i * 22, 380 - i * 22);
            graphics.DrawLines(contour, new[] { new Drawing.Point(920, 740), new Drawing.Point(1250, 280), new Drawing.Point(1460, 700), new Drawing.Point(1740, 370) });
            graphics.DrawString("1440p / 4K preview", font, white, 1030, 790);
        }
        return frame;
    }
}
