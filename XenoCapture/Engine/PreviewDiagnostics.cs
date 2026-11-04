using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace XenoCapture.Engine;
internal static class PreviewDiagnostics
{
    public static Dictionary<string, object> Run()
    {
        using var apartment = new Apartment();
        using var gpu = new D3DPreview(0, 1280, 720);
        using var rgb = new Packet(320 * 240 * 4, false, 320, 240);
        for (int i = 0; i < rgb.Length; i += 4) { rgb.Bytes[i] = 22; rgb.Bytes[i + 1] = 44; rgb.Bytes[i + 2] = 175; rgb.Bytes[i + 3] = 255; }
        gpu.Draw(rgb, 0.25); var rgba = gpu.Readback();
        int center = (360 * 1280 + 640) * 4, border = (360 * 1280 + 40) * 4;
        if (Math.Abs(rgba[center] - 22) > 2 || Math.Abs(rgba[center + 1] - 44) > 2 || Math.Abs(rgba[center + 2] - 175) > 2 || rgba[border] != 0 || rgba[border + 2] != 0) throw new InvalidOperationException("RGB GPU scaling/letterboxing produced incorrect pixels.");
        using var nv12 = new Packet(320 * 240 * 3 / 2, false, 320, 240, PixelLayout.Nv12);
        Array.Fill(nv12.Bytes, (byte)63, 0, 320 * 240);
        for (int i = 320 * 240; i < nv12.Length; i += 2) { nv12.Bytes[i] = 102; nv12.Bytes[i + 1] = 240; }
        gpu.Draw(nv12, 0.25); var yuv = gpu.Readback();
        if (yuv[center + 2] < 250 || yuv[center + 1] > 4 || yuv[center] > 4) throw new InvalidOperationException("NV12 GPU color conversion produced incorrect pixels.");
        using var converted = PixelConversion.ToBgra(nv12);
        if (converted.Bytes[2] < 250 || converted.Bytes[1] > 4 || converted.Bytes[0] > 4) throw new InvalidOperationException("NV12 snapshot conversion produced incorrect pixels.");
        Array.Fill(nv12.Bytes, (byte)16, 0, 320 * 120); Array.Fill(nv12.Bytes, (byte)235, 320 * 120, 320 * 120);
        Array.Fill(nv12.Bytes, (byte)128, 320 * 240, 320 * 120); gpu.Draw(nv12, 0); yuv = gpu.Readback();
        if (yuv[(40 * 1280 + 640) * 4] > 3 || yuv[(680 * 1280 + 640) * 4] < 252) throw new InvalidOperationException("NV12 range or image orientation is incorrect.");
        nv12.FullRange = true; gpu.Draw(nv12, 0); yuv = gpu.Readback();
        if (Math.Abs(yuv[(40 * 1280 + 640) * 4] - 16) > 2 || Math.Abs(yuv[(680 * 1280 + 640) * 4] - 235) > 2) throw new InvalidOperationException("Changing the NV12 range did not update shader constants.");
        Array.Fill(nv12.Bytes, (byte)100, 0, 320 * 240);
        for (int i = 320 * 240; i < nv12.Length; i += 2) { nv12.Bytes[i] = 90; nv12.Bytes[i + 1] = 155; }
        foreach (bool full in new[] { false, true })
        foreach (bool bt601 in new[] { false, true })
        {
            nv12.FullRange = full; nv12.Bt601 = bt601; gpu.Draw(nv12, 0); yuv = gpu.Readback();
            using var reference = PixelConversion.ToBgra(nv12);
            for (int channel = 0; channel < 3; channel++) if (Math.Abs(yuv[center + channel] - reference.Bytes[channel]) > 2) throw new InvalidOperationException("Changing color matrix/range produced stale GPU constants.");
        }
        gpu.Resize(64, 64);
        using (var edge = new Packet(64 * 64 * 4, false, 64, 64))
        {
            Array.Fill(edge.Bytes, (byte)40, 0, edge.Length);
            int point = (32 * 64 + 32) * 4 + 2;
            edge.Bytes[point] = 100; edge.Bytes[point + 64 * 4] = 120; edge.Bytes[point + 4] = 60; edge.Bytes[point - 4] = 90;
            gpu.Draw(edge, 0); int unsharpened = gpu.Readback()[point];
            gpu.Draw(edge, 1); int sharpened = gpu.Readback()[point];
            if (Math.Abs(unsharpened - 100) > 2 || sharpened < unsharpened + 10) throw new InvalidOperationException("The sharpness slider did not update its GPU constants.");
        }
        using var fullHd = new Packet(1920 * 1080 * 3 / 2, false, 1920, 1080, PixelLayout.Nv12);
        Array.Fill(fullHd.Bytes, (byte)128, 0, fullHd.Length); gpu.Resize(1920, 1080);
        gpu.Draw(fullHd, 0.25);
        var fitted = gpu.Readback();
        gpu.RetainFullscreenOutput = true;
        gpu.PresentationScale = .78f;
        gpu.Draw(fullHd, 0.25);
        var popped = gpu.Readback();
        int fullCenter = (540 * 1920 + 960) * 4;
        if (popped[0] != 0 || popped[3] != 255 || popped[fullCenter] != fitted[fullCenter] || fitted[0] < 20)
            throw new InvalidOperationException("Fullscreen pop did not preserve the image with a cleared border.");
        gpu.PresentationScale = 1;
        gpu.RedrawFullscreenMotion();
        if (!gpu.CanRedrawFullscreenMotion || !fitted.AsSpan().SequenceEqual(gpu.Readback()))
            throw new InvalidOperationException("Fullscreen pop did not restore the normal output pixels.");
        var landing = new System.Drawing.Rectangle(78, 212, 640, 480);
        gpu.RedrawFullscreenMotion(landing);
        byte[] docked = gpu.Readback();
        int leftOutside = ((landing.Y + landing.Height / 2) * 1920 + landing.X - 1) * 4;
        int leftInside = leftOutside + 4;
        int topBar = (landing.Y * 1920 + landing.X + landing.Width / 2) * 4;
        if (docked[leftOutside] != 0 || docked[leftInside] != fitted[fullCenter] || docked[topBar] != 0 || docked[fullCenter] != 0)
            throw new InvalidOperationException("The landing viewport did not translate, clip and letterbox the fullscreen image.");
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < 120; i++) gpu.Draw(fullHd, 0.25);
        gpu.Readback(); double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return new() { ["direct3DReadback"] = "pass", ["fullscreenPopViewportAndSettledPixels"] = "pass", ["gpuLiveSharpnessAndColorSettings"] = "pass", ["gpuRgbScalingAndLetterbox"] = "pass", ["gpuNv12ColorAndOrientation"] = "pass", ["snapshotNv12Conversion"] = "pass", ["gpu1080pSubmissionAndCompletionMeanMs"] = elapsed / 120, ["gpuBenchmarkFrames"] = 120, ["gpuBenchmarkIncludesMonitorPresentation"] = false };
    }
}
