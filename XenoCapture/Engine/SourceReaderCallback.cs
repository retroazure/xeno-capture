using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace XenoCapture.Engine;

[ComVisible(true)]
[Guid("deec8d99-fa1d-4d82-84c2-2c8969944867")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ISourceReaderCallback
{
    [PreserveSig] int OnReadSample(int status, uint stream, uint flags, long timestamp, nint sample);
    [PreserveSig] int OnFlush(uint stream);
    [PreserveSig] int OnEvent(uint stream, nint mediaEvent);
}

// One asynchronous read is outstanding. The callback only hands its sample to
// the capture worker. All source reader calls remain on that worker.
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class SourceReaderCallback : ISourceReaderCallback, IDisposable
{
    static readonly Guid AsyncCallback = new("1e3dbeac-bb43-4c35-b507-cd644464c965");
    readonly object gate = new();
    readonly AutoResetEvent ready = new(false), flushed = new(false);
    readonly CancellationToken cancellation;
    readonly WaitHandle[] waits;
    bool reading, disposed;
    nint sample;
    int status;
    uint flags;
    long timestamp;

    internal SourceReaderCallback(CancellationToken cancellation)
    {
        this.cancellation = cancellation;
        waits = [ready, cancellation.WaitHandle];
    }

    internal void Attach(Com attributes)
    {
        nint callback = Marshal.GetComInterfaceForObject(this, typeof(ISourceReaderCallback));
        try { attributes.SetUnknown(AsyncCallback, callback); }
        finally { Marshal.Release(callback); }
    }

    internal Com? Read(Com reader, uint stream, out uint resultFlags, out long resultTimestamp)
    {
        cancellation.ThrowIfCancellationRequested();
        lock (gate) reading = true;
        try { Native.RequestSample(reader, stream); }
        catch { lock (gate) reading = false; throw; }

        WaitHandle.WaitAny(waits);
        cancellation.ThrowIfCancellationRequested();
        lock (gate)
        {
            Native.Hr(status);
            var owned = sample;
            sample = 0;
            resultFlags = flags;
            resultTimestamp = timestamp;
            return owned == 0 ? null : new Com(owned);
        }
    }

    internal void FlushPending(Com reader, uint stream)
    {
        lock (gate) { if (!reading) return; }
        if (Native.FlushReader(reader, stream) >= 0) flushed.WaitOne(250);
    }

    int ISourceReaderCallback.OnReadSample(int result, uint stream, uint readFlags, long time, nint frame)
    {
        lock (gate)
        {
            if (disposed || !reading) return 0;
            reading = false;
            status = result;
            flags = readFlags;
            timestamp = time;
            if (result >= 0 && frame != 0)
            {
                Marshal.AddRef(frame);
                sample = frame;
            }
            ready.Set();
            return 0;
        }
    }

    int ISourceReaderCallback.OnFlush(uint stream)
    {
        lock (gate) { if (!disposed) flushed.Set(); }
        return 0;
    }

    int ISourceReaderCallback.OnEvent(uint stream, nint mediaEvent) => 0;

    public void Dispose()
    {
        nint owned;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            owned = sample;
            sample = 0;
        }
        if (owned != 0) Marshal.Release(owned);
        ready.Dispose();
        flushed.Dispose();
    }
}
