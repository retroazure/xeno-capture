using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace XenoCapture.Engine;
internal sealed class TestCaptureBackend : ICaptureBackend
{
    readonly SharedOperation shutdown = new();
    readonly ManualResetEventSlim openGate, readGate = new(false), shutdownGate;
    int disconnected, disposed, readActive, frames, shutdownCalls;
    readonly long origin = Clock.Now;
    public readonly TaskCompletionSource OpenEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), ReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), SecondRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Produce { get; init; }
    public bool OneFrame { get; init; }
    public bool IgnoreOpenCancellation { get; init; }
    public int DisposeCount { get; private set; }
    public int ShutdownCount => Volatile.Read(ref shutdownCalls);
    public PixelLayout Layout => PixelLayout.Bgr32;
    public int AudioRate => 48000;
    public int AudioChannels => 2;
    public TestCaptureBackend(bool delayOpen = false, bool blockShutdown = false)
    { openGate = new(!delayOpen); shutdownGate = new(!blockShutdown); }
    public void Open(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested(); OpenEntered.TrySetResult();
        if (IgnoreOpenCancellation) openGate.Wait(); else openGate.Wait(cancellation);
        cancellation.ThrowIfCancellationRequested();
    }
    public Packet? Read()
    {
        Interlocked.Increment(ref readActive);
        try
        {
            if (Volatile.Read(ref disposed) != 0) throw new InvalidOperationException("Read after release");
            ReadEntered.TrySetResult(); if (Volatile.Read(ref frames) > 0) SecondRead.TrySetResult();
            if (!Produce || (OneFrame && Volatile.Read(ref frames) > 0)) readGate.Wait();
            else Thread.Sleep(8);
            if (Volatile.Read(ref disconnected) != 0) throw new IOException("Synthetic device unplugged");
            if (Volatile.Read(ref shutdownCalls) != 0) return null;
            int index = Interlocked.Increment(ref frames);
            var frame = new Packet(320 * 180 * 4, false, 320, 180) { Time = origin + (index - 1) * 333333L, Duration = 333333 };
            Array.Fill(frame.Bytes, (byte)(index % 128 + 40), 0, frame.Length); return frame;
        }
        finally { Interlocked.Decrement(ref readActive); }
    }
    public void Unplug() { Volatile.Write(ref disconnected, 1); readGate.Set(); }
    public void ReleaseBlockedCalls() { shutdownGate.Set(); openGate.Set(); readGate.Set(); }
    public void Shutdown() => shutdown.Run(() => Task.Run(() =>
    {
        Interlocked.Increment(ref shutdownCalls); shutdownGate.Wait(); readGate.Set();
    })).GetAwaiter().GetResult();
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (Volatile.Read(ref readActive) != 0) throw new InvalidOperationException("Released while a read is active");
        Shutdown(); DisposeCount++;
        // Events remain available to the test's late-release trigger; the
        // backend itself performs no native allocation or physical capture.
    }
}

