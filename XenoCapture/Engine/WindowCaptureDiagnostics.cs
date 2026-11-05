using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Windows.Graphics.Capture;

namespace XenoCapture.Engine;

internal static class WindowCaptureDiagnostics
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    internal static async Task Run(string directory)
    {
        if (!GraphicsCaptureSession.IsSupported())
            throw new InvalidOperationException("Windows Graphics Capture is unavailable on this test machine.");

        int initialBuffers = Packet.LiveBuffers;
        var target = new Window
        {
            Title = "Xeno Capture synthetic receiver",
            Width = 320,
            Height = 220,
            WindowStyle = WindowStyle.None,
            ShowActivated = false,
            ShowInTaskbar = false,
            Background = Brushes.DeepSkyBlue
        };
        target.Show();

        bool targetClosed = false;
        try
        {
            nint window = new WindowInteropHelper(target).Handle;
            var first = new TaskCompletionSource<(int Width, int Height)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var resized = new TaskCompletionSource<(int Width, int Height)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstInput = NewInput(window);
            int frameCount = 0;
            firstInput.Data += packet =>
            {
                Interlocked.Increment(ref frameCount);
                int center = (packet.Width * (packet.Height / 2) + packet.Width / 2) * 4;
                byte blue = packet.Bytes[center], red = packet.Bytes[center + 2];
                if (blue > 150 && red < 80) first.TrySetResult((packet.Width, packet.Height));
                if (red > 150 && blue < 80 && first.Task.IsCompletedSuccessfully && packet.Width > first.Task.Result.Width && packet.Height > first.Task.Result.Height)
                    resized.TrySetResult((packet.Width, packet.Height));
            };
            firstInput.Failed += error => { first.TrySetException(error); resized.TrySetException(error); };
            (int Width, int Height) initial, updated;
            try
            {
                await firstInput.Start().WaitAsync(Timeout);
                initial = await first.Task.WaitAsync(Timeout);
                target.Width = 420;
                target.Height = 280;
                target.Background = Brushes.OrangeRed;
                try { updated = await resized.Task.WaitAsync(Timeout); }
                catch (TimeoutException)
                {
                    throw new InvalidOperationException($"The receiver resize did not produce a matching frame after {Volatile.Read(ref frameCount)} frames. The test window measured {target.ActualWidth} × {target.ActualHeight}.");
                }
                if (updated.Width <= initial.Width || updated.Height <= initial.Height)
                    throw new InvalidOperationException("The window capture did not apply the new frame size.");
            }
            finally { await firstInput.Stop(); }
            int stoppedCount = Volatile.Read(ref frameCount);
            target.Background = Brushes.DeepSkyBlue;
            await Task.Delay(150);
            if (Volatile.Read(ref frameCount) != stoppedCount)
                throw new InvalidOperationException("A frame was published after window capture stopped.");

            var secondInput = NewInput(window);
            var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var closed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            secondInput.Data += _ => restarted.TrySetResult();
            secondInput.Failed += error => { restarted.TrySetException(error); closed.TrySetResult(error); };
            try
            {
                await secondInput.Start().WaitAsync(Timeout);
                await restarted.Task.WaitAsync(Timeout);
                target.Close();
                targetClosed = true;
                var reason = await closed.Task.WaitAsync(Timeout);
                if (!reason.Message.Contains("preview window closed", StringComparison.Ordinal))
                    throw new InvalidOperationException("Closing the test window did not retire its capture session.", reason);
            }
            finally { await secondInput.Stop(); }

            if (Packet.LiveBuffers != initialBuffers)
                throw new InvalidOperationException("A window capture frame buffer was retained after stopping.");
            File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new
            {
                passed = true,
                initialWidth = initial.Width,
                initialHeight = initial.Height,
                resizedWidth = updated.Width,
                resizedHeight = updated.Height,
                disconnected = true,
                receiverClosed = true,
                retainedBuffers = Packet.LiveBuffers - initialBuffers
            }));
        }
        finally
        {
            if (!targetClosed) target.Close();
        }
    }

    static CaptureStream NewInput(nint window)
    {
        var item = WindowCaptureBackend.CreateItem(window);
        return new CaptureStream(() => new WindowCaptureBackend(item), TimeSpan.FromSeconds(2));
    }
}
