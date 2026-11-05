using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace XenoCapture.Engine;

// Event-driven WASAPI playback at the shared engine's minimum default buffer.
// Audio input is captured separately; this queue always favors recent audio.
internal sealed class AudioMonitor : IDisposable
{
    [StructLayout(LayoutKind.Sequential, Pack = 2)] struct Format { public ushort Tag, Channels; public uint Rate, BytesPerSecond; public ushort Align, Bits, Extra; }
    readonly ConcurrentQueue<Packet> queue = new();
    readonly AutoResetEvent audioEvent = new(false);
    readonly object gate = new();
    readonly Thread thread;
    readonly SharedOperation stop = new();
    readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly int rate, channels;
    volatile bool stopping;
    bool stopRequested;
    readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Exception? failure;
    long queuedBytes;
    long framesWritten;
    public long FramesWritten => Interlocked.Read(ref framesWritten);
    public AudioMonitor(int rate, int channels) : this(rate, channels, true) { }
    AudioMonitor(int rate, int channels, bool wait)
    {
        this.rate = rate; this.channels = channels;

        thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "Xeno audio output", Priority = ThreadPriority.AboveNormal };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { if (wait) ready.Task.GetAwaiter().GetResult(); } catch { stopping = true; try { Stop().GetAwaiter().GetResult(); } catch { } throw; }
    }
    public static AudioMonitor BeginStart(int rate, int channels) => new(rate, channels, false);
    public Task Ready => ready.Task;
    public void Submit(Packet packet)
    {
        lock (gate)
        {
            if (stopRequested) return;
            if (failure != null) throw new InvalidOperationException("The Windows audio output stopped.", failure);
            while (Interlocked.Read(ref queuedBytes) > rate * channels * 2 / 25 && queue.TryDequeue(out var old))
            { Interlocked.Add(ref queuedBytes, -old.Length); old.Dispose(); }
            queue.Enqueue(packet.Retain()); Interlocked.Add(ref queuedBytes, packet.Length);
        }
    }
    void Run(TaskCompletionSource ready)
    {
        bool initialized = false;
        try { using var scheduling = MultimediaScheduling.Enter("Audio"); Native.Hr(Native.CoInitializeEx(0, 2)); initialized = true; RunCore(ready); }
        catch (Exception ex) { failure = ex; ready.TrySetException(ex); }
        finally
        {
            lock (gate) { stopping = true; while (queue.TryDequeue(out var p)) p.Dispose(); }
            if (initialized) Native.CoUninitialize();
            audioEvent.Dispose(); completed.TrySetResult();
        }
    }
    unsafe void RunCore(TaskCompletionSource ready)
    {
        Guid clientId = new("1cb9ad4c-dbfa-4c32-b178-c2f568a703b2");
        Guid renderId = new("f294acfc-3146-4483-a7bf-addca7c260e2");
        Native.Hr(Native.CreateAudioEndpointEnumerator(out nint endpointPointer));
        using var enumerator = new Com(endpointPointer);

        const int render = 0, console = 0;
        nint devicePointer;
        Native.Hr(((delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)enumerator.Slot(4))
            (endpointPointer, render, console, &devicePointer));
        using var device = new Com(devicePointer);

        nint cp;
        Native.Hr(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)device.Slot(3))
            (devicePointer, &clientId, 23, 0, &cp));
        using var client = new Com(cp);
        var format = new Format { Tag = 1, Channels = (ushort)channels, Rate = (uint)rate, BytesPerSecond = (uint)(rate * channels * 2), Align = (ushort)(channels * 2), Bits = 16 };
        const uint eventCallback = 0x40000, autoConvertPcm = 0x80000000, resampleQuality = 0x08000000;
        Native.Hr(((delegate* unmanaged[Stdcall]<nint, int, uint, long, long, Format*, nint, int>)client.Slot(3))(cp, 0, eventCallback | autoConvertPcm | resampleQuality, 0, 0, &format, 0));
        Native.Hr(((delegate* unmanaged[Stdcall]<nint, nint, int>)client.Slot(13))(cp, audioEvent.SafeWaitHandle.DangerousGetHandle()));
        uint frames; Native.Hr(((delegate* unmanaged[Stdcall]<nint, uint*, int>)client.Slot(4))(cp, &frames));
        nint rp; Native.Hr(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)client.Slot(14))(cp, &renderId, &rp)); using var renderer = new Com(rp);
        Packet? current = null; int offset = 0;
        Native.Hr(client.Call(10)); ready.TrySetResult();
        try
        {
            while (!stopping)
            {
                audioEvent.WaitOne(30); if (stopping) break;
                uint padding; Native.Hr(((delegate* unmanaged[Stdcall]<nint, uint*, int>)client.Slot(6))(cp, &padding));
                uint available = frames - Math.Min(frames, padding); if (available == 0) continue;
                nint data; Native.Hr(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)renderer.Slot(3))(rp, available, &data));
                var target = new Span<byte>((void*)data, checked((int)available * format.Align)); target.Clear(); int written = 0;
                try
                {
                    while (written < target.Length)
                    {
                        if (current == null)
                        {
                            if (!queue.TryDequeue(out current)) break;
                            Interlocked.Add(ref queuedBytes, -current.Length); offset = 0;
                        }
                        int amount = Math.Min(target.Length - written, current.Length - offset);
                        current.Bytes.AsSpan(offset, amount).CopyTo(target.Slice(written, amount)); written += amount; offset += amount;
                        if (offset >= current.Length) { current.Dispose(); current = null; }
                    }
                }
                finally { Native.Hr(((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)renderer.Slot(4))(rp, available, written == 0 ? 2u : 0u)); Interlocked.Add(ref framesWritten, written / format.Align); }
            }
        }
        finally { current?.Dispose(); client.Call(11); client.Call(12); }
    }
    public Task Stop() => stop.Run(async () =>
    {
        lock (gate) { stopRequested = true; stopping = true; ready.TrySetCanceled(); if (!completed.Task.IsCompleted) { try { audioEvent.Set(); } catch (ObjectDisposedException) { } } }
        try { await completed.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { throw new InvalidOperationException("The audio output driver did not finish shutdown."); }
    });
    public void Dispose() => Stop().GetAwaiter().GetResult();
}




