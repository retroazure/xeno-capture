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
                JsonSerializer.Serialize(new { passed = true, isolatedListeners = true, stoppedWhileWaiting = true, physicalPhone = false }));
        }
        finally
        {
            await Task.WhenAll(first.Stop(), second.Stop());
        }
    }

    static bool IsListening(AndroidUsbCaptureBackend backend)
        => AndroidProcessJob.FindLoopbackPort(backend.ServerProcessId) == backend.ServerPort;
}
