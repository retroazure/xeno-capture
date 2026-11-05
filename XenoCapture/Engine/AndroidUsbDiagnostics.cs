using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace XenoCapture.Engine;

internal static class AndroidUsbDiagnostics
{
    internal static async Task Run(string directory)
    {
        var firstBackend = new AndroidUsbCaptureBackend(detectUsb: false);
        var secondBackend = new AndroidUsbCaptureBackend(detectUsb: false);
        using var smooth = new AndroidUsbCaptureBackend(detectUsb: false, profile: AndroidPreviewProfile.Smooth);
        using var highDetail = new AndroidUsbCaptureBackend(detectUsb: false, playAudio: true, profile: AndroidPreviewProfile.HighDetail);
        CheckPreviewOptions(firstBackend, 1280, 30, "4M", playAudio: false);
        CheckPreviewOptions(smooth, 1280, 60, "5M", playAudio: false);
        CheckPreviewOptions(highDetail, 1920, 60, "8M", playAudio: true);
        await MainWindow.CheckPhonePreviewDefaults();
        var first = new CaptureStream(() => firstBackend, TimeSpan.FromSeconds(2));
        var second = new CaptureStream(() => secondBackend, TimeSpan.FromSeconds(2));
        try
        {
            await first.Start().WaitAsync(TimeSpan.FromSeconds(7));
            await second.Start().WaitAsync(TimeSpan.FromSeconds(7));
            int firstPort = firstBackend.ServerPort, secondPort = secondBackend.ServerPort;
            if (firstPort == 0 || secondPort == 0 || firstPort == secondPort)
                throw new InvalidOperationException("Android USB sessions did not get separate ports.");
            if (!IsListening(firstBackend) || !IsListening(secondBackend))
                throw new InvalidOperationException("An isolated ADB listener did not start.");

            await first.Stop();
            if (IsListening(firstBackend))
                throw new InvalidOperationException("The first ADB listener stayed open after disconnect.");
            if (!IsListening(secondBackend))
                throw new InvalidOperationException("The second ADB listener stopped when the first disconnected.");
            await second.Stop();
            if (IsListening(secondBackend))
                throw new InvalidOperationException("The second ADB listener stayed open after disconnect.");

            File.WriteAllText(Path.Combine(directory, "results.json"),
                JsonSerializer.Serialize(new { passed = true, previewProfiles = true, aiOffByDefault = true,
                    isolatedListeners = true, stoppedWhileWaiting = true, physicalPhone = false }));
        }
        finally
        {
            await Task.WhenAll(first.Stop(), second.Stop());
        }
    }

    static void CheckPreviewOptions(AndroidUsbCaptureBackend backend, int size, int fps, string bitrate, bool playAudio)
    {
        var args = backend.CreatePreviewProcess("Diagnostic phone preview").ArgumentList;
        if (!args.Contains($"--max-size={size}") || !args.Contains($"--max-fps={fps}")
            || !args.Contains("--video-bit-rate=" + bitrate) || !args.Contains("--video-buffer=0")
            || !args.Contains("--select-usb") || !args.Contains("--no-control") || !args.Contains("--no-clipboard-autosync")
            || args.Contains("--audio-source=output") != playAudio || args.Contains("--no-audio") == playAudio)
            throw new InvalidOperationException("The Android preview did not apply the expected launch options.");
    }

    static bool IsListening(AndroidUsbCaptureBackend backend)
        => AndroidProcessJob.FindLoopbackPort(backend.ServerProcessId) == backend.ServerPort;
}
