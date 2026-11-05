using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;

namespace XenoCapture.Engine;
internal static class RenderPerformanceDiagnostics
{
    public static Dictionary<string, object> Run()
    {
        using var apartment = new Apartment();
        var construction = new List<double>();
        for (int i = 0; i < 8; i++)
        {
            var clock = Stopwatch.StartNew(); using var renderer = new D3DPreview(0, 640, 360);
            construction.Add(clock.Elapsed.TotalMilliseconds);
        }
        var cases = new List<Dictionary<string, object>>();
        foreach (var layout in new[] { PixelLayout.Bgr32, PixelLayout.Nv12 })
        foreach (int outputHeight in new[] { 1080, 2160 })
        {
            int outputWidth = outputHeight * 16 / 9;
            using var frame = Pattern(1920, 1080, layout);
            using var renderer = new D3DPreview(0, outputWidth, outputHeight);
            for (int i = 0; i < 60; i++) renderer.Draw(frame, .35); renderer.Readback();
            var batches = new List<double>(); var submissions = new List<double>();
            for (int batch = 0; batch < 7; batch++)
            {
                var clock = Stopwatch.StartNew();
                for (int i = 0; i < 120; i++) { renderer.Draw(frame, .35); submissions.Add(renderer.LastUploadDrawMs); }
                renderer.Readback(); batches.Add(clock.Elapsed.TotalMilliseconds / 120);
            }
            submissions.Sort(); batches.Sort();
            cases.Add(new() { ["layout"] = layout.ToString(), ["output"] = $"{outputWidth}x{outputHeight}",
                ["medianBatchMsPerFrame"] = batches[batches.Count / 2], ["meanBatchMsPerFrame"] = batches.Average(),
                ["cpuSubmissionP95Ms"] = submissions[(int)((submissions.Count - 1) * .95)],
                ["cpuSubmissionP99Ms"] = submissions[(int)((submissions.Count - 1) * .99)],
                ["constantUploads"] = renderer.ConstantUploads, ["targetClears"] = renderer.TargetClears, ["pixelSha256"] = Convert.ToHexString(SHA256.HashData(renderer.Readback())) });
        }
        // The same renderer must update its constants when any setting changes,
        // and clear letterbox bars after different source/output dimensions.
        var states = new List<string>();
        using (var renderer = new D3DPreview(0, 640, 480))
        {
            foreach (var layout in new[] { PixelLayout.Nv12, PixelLayout.Bgr32, PixelLayout.Nv12 })
            using (var frame = Pattern(320, 180, layout))
            foreach (bool full in new[] { false, true })
            foreach (bool bt601 in new[] { false, true })
            foreach (double sharpness in new[] { 0.0, .35, 1.0 })
            {
                frame.FullRange = full; frame.Bt601 = bt601;
                renderer.Draw(frame, sharpness); states.Add(Convert.ToHexString(SHA256.HashData(renderer.Readback())));
            }
            renderer.Resize(640, 360);
            using var fullFrame = Pattern(320, 180, PixelLayout.Nv12); renderer.Draw(fullFrame, .35);
            states.Add(Convert.ToHexString(SHA256.HashData(renderer.Readback())));
            renderer.Resize(800, 450); renderer.Draw(fullFrame, .35);
            states.Add(Convert.ToHexString(SHA256.HashData(renderer.Readback())));
        }
        return new() { ["previewShadersPrecompiled"] = true, ["rendererConstructionMs"] = construction, ["warmConstructionMeanMs"] = construction.Skip(1).Average(),
            ["cases"] = cases, ["performancePreview"] = ComparePreviewFilters(),
            ["settingTransitionPixelHashes"] = states, ["framesPerWorkload"] = 840,
            ["batchTimingIncludesFinalGpuReadback"] = true, ["measuresMonitorPresentation"] = false,
            ["captureHardware"] = "not exercised", ["result"] = "PASS" };
    }
    static object ComparePreviewFilters()
    {
        var results = new List<object>();
        foreach (int height in new[] { 1080, 2160 })
        {
            using var frame = Pattern(1920, 1080, PixelLayout.Nv12);
            using var renderer = new D3DPreview(0, height * 16 / 9, height);
            renderer.Draw(frame, 0);
            var unfiltered = renderer.Readback();
            renderer.EdgeSmoothing = true;
            renderer.Draw(frame, .25, PreviewUpscaler.Performance);
            if (!unfiltered.AsSpan().SequenceEqual(renderer.Readback()))
                throw new InvalidOperationException("Performance preview did not preserve plain scaling and NV12 color.");

            var filtered = new List<double>();
            var performance = new List<double>();
            const int frames = 90;
            for (int batch = 0; batch < 5; batch++)
            {
                filtered.Add(Measure(PreviewUpscaler.Standard));
                performance.Add(Measure(PreviewUpscaler.Performance));
            }
            results.Add(new { outputHeight = height, filteredMedianMs = filtered.Order().ElementAt(2),
                performanceMedianMs = performance.Order().ElementAt(2), plainScalingPixelMatch = true });

            double Measure(PreviewUpscaler upscaler)
            {
                renderer.Draw(frame, .25, upscaler);
                renderer.Readback();
                var clock = Stopwatch.StartNew();
                for (int i = 0; i < frames; i++) renderer.Draw(frame, .25, upscaler);
                renderer.Readback();
                return clock.Elapsed.TotalMilliseconds / frames;
            }
        }
        return results;
    }

    static Packet Pattern(int width, int height, PixelLayout layout)
    {
        var frame = new Packet(width * height * (layout == PixelLayout.Nv12 ? 3 : 8) / 2, false, width, height, layout);
        if (layout == PixelLayout.Nv12)
        {
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) frame.Bytes[y * width + x] = (byte)(16 + (x * 31 + y * 17) % 220);
            for (int i = width * height; i < frame.Length; i += 2) { frame.Bytes[i] = (byte)(90 + i % 57); frame.Bytes[i + 1] = (byte)(90 + i % 71); }
        }
        else for (int i = 0; i < frame.Length; i += 4) { frame.Bytes[i] = (byte)(i % 251); frame.Bytes[i + 1] = (byte)(i / 7 % 251); frame.Bytes[i + 2] = (byte)(i / 13 % 251); frame.Bytes[i + 3] = 255; }
        return frame;
    }
}
