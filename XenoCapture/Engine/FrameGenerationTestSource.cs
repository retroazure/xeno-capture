using System;
using System.Threading;

namespace XenoCapture.Engine;
internal sealed class FrameGenerationTestSource(int framesPerSecond = 30, int gameFramesPerSecond = 0) : ICaptureBackend
{
    readonly object gate = new(); readonly AutoResetEvent stopped = new(false);
    FrameDeadlineWaiter? timer; bool disposed; volatile bool stopping;
    long next, origin, frames, uniqueFrames, lastGameIndex, heldGameIndex;
    int holdMotion, pauseFrames; bool holding;
    internal bool HoldMotion { get => Volatile.Read(ref holdMotion)!=0; set => Volatile.Write(ref holdMotion,value ? 1:0); }
    internal bool PauseFrames { get => Volatile.Read(ref pauseFrames)!=0; set => Volatile.Write(ref pauseFrames,value ? 1:0); }
    internal long UniqueFrames => Interlocked.Read(ref uniqueFrames);
    public long Frames => Interlocked.Read(ref frames);
    public PixelLayout Layout => PixelLayout.Bgr32;
    public int AudioRate => 0;
    public int AudioChannels => 0;
    public void Open(CancellationToken cancellation) { cancellation.ThrowIfCancellationRequested(); timer = new(stopped); origin = next = Clock.Now; }
    public Packet? Read()
    {
        while (!stopping && (Clock.Now < next || PauseFrames)) timer!.WaitUntil(PauseFrames ? Clock.Now+100_000 : next);
        if (stopping) return null;
        long index = Interlocked.Increment(ref frames), period = 10_000_000 / framesPerSecond;
        long gameIndex=gameFramesPerSecond>0 ? (index-1)*gameFramesPerSecond/framesPerSecond+1 : index;
        if (HoldMotion) { if (!holding) heldGameIndex=gameIndex; gameIndex=heldGameIndex; holding=true; } else holding=false;
        if (gameIndex!=lastGameIndex) Interlocked.Increment(ref uniqueFrames); lastGameIndex=gameIndex;
        var packet = FrameGenerationDiagnostics.Pattern((int)(gameIndex * 16 % 640));
        packet.Time = origin + (index - 1) * period; packet.Duration = period; next = Math.Max(origin + index * period, Clock.Now);
        return packet;
    }
    public void Shutdown() { lock (gate) { if (!disposed) { stopping = true; stopped.Set(); } } }
    public void Dispose() { lock (gate) { if (disposed) return; stopping = true; timer?.Dispose(); stopped.Dispose(); disposed = true; } }
}
