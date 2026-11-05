using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XenoCapture.Engine;

internal sealed record CaptureDevice(string Name, string Id, bool Audio)
{
    public override string ToString() => Name;
}
internal sealed record VideoMode(uint Index, int Width, int Height, int Numerator, int Denominator, Guid Subtype)
{
    public double Fps => (double)Numerator / Math.Max(1, Denominator);
    public override string ToString()
    {
        var fourcc = Encoding.ASCII.GetString(Subtype.ToByteArray(), 0, 4);
        var format = Subtype == Keys.Rgb32 ? "RGB" : fourcc.All(c => c >= 32 && c < 127) ? fourcc : "RAW";
        return $"{Width} × {Height}  ·  {Fps:0.##} fps  ·  {format}";
    }
}
internal enum PixelLayout { Bgr32, Nv12 }
internal sealed class Packet : IDisposable
{
    sealed class Storage
    {
        int references = 1;
        public readonly byte[] Bytes;
        readonly bool clearOnReturn;
        public Storage(int length, bool clearOnReturn)
        {
            this.clearOnReturn = clearOnReturn;
            Bytes = ArrayPool<byte>.Shared.Rent(length);
            Interlocked.Increment(ref liveBuffers);
        }
        public void AddRef()
        {
            int count = Volatile.Read(ref references);
            while (count > 0)
            {
                int found = Interlocked.CompareExchange(ref references, checked(count + 1), count);
                if (found == count) return; count = found;
            }
            throw new ObjectDisposedException(nameof(Packet));
        }
        public void Release()
        {
            if (Interlocked.Decrement(ref references) == 0) { ArrayPool<byte>.Shared.Return(Bytes, clearOnReturn); Interlocked.Decrement(ref liveBuffers); }
        }
    }
    static int liveBuffers;
    public static int LiveBuffers => Volatile.Read(ref liveBuffers);
    readonly Storage storage;
    int disposed;
    public PixelLayout Layout { get; }
    public bool FullRange { get; set; }
    public bool Bt601 { get; set; }
    public byte[] Bytes => Volatile.Read(ref disposed) == 0 ? storage.Bytes : throw new ObjectDisposedException(nameof(Packet));
    public int Length { get; }
    public long Time { get; set; }
    public long Duration { get; set; }
    public bool Audio { get; }
    public int Width { get; }
    public int Height { get; }
    public Packet(int length, bool audio = false, int width = 0, int height = 0, PixelLayout layout = PixelLayout.Bgr32, bool clearOnReturn = false)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        storage = new(length, clearOnReturn); Layout = layout; Length = length; Audio = audio; Width = width; Height = height;
    }
    Packet(Packet source)
    {
        storage = source.storage; Layout = source.Layout; Length = source.Length; Audio = source.Audio; Width = source.Width; Height = source.Height;
        Time = source.Time; Duration = source.Duration; FullRange = source.FullRange; Bt601 = source.Bt601;
    }
    internal bool IsExactVideoRepeatOf(Packet? previous)
    {
        // Published capture buffers are immutable. A diagnostic may reuse
        // and mutate its own buffer, so compare only independent storage.
        return previous != null && !Audio && !previous.Audio && !ReferenceEquals(storage, previous.storage)
            && Width == previous.Width && Height == previous.Height && Layout == previous.Layout
            && Length == previous.Length && FullRange == previous.FullRange && Bt601 == previous.Bt601
            && Bytes.AsSpan(0, Length).SequenceEqual(previous.Bytes.AsSpan(0, previous.Length));
    }
    public Packet Retain()
    {
        if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(Packet));
        storage.AddRef(); return new Packet(this);
    }
    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) storage.Release(); }
}
internal static class CaptureDevices
{
    public static unsafe List<CaptureDevice> List(bool audio)
    {
        using var usage = MediaFoundationUsage.Enter(); using var apartment = new Apartment(); using var attrs = Native.Attributes();
        attrs.Guid(Keys.SourceType, audio ? Keys.AudioSource : Keys.VideoSource);
        Native.Hr(Native.MFEnumDeviceSources(attrs.P, out var array, out var count));
        var devices = new List<CaptureDevice>();
        try
        {
            for (int i = 0; i < count; i++)
            {
                using var activate = new Com(Marshal.ReadIntPtr(array, i * nint.Size));
                Marshal.WriteIntPtr(array, i * nint.Size, 0);
                devices.Add(new(activate.String(Keys.Name), activate.String(audio ? Keys.AudioLink : Keys.VideoLink), audio));
            }
        }
        finally { for (int i = 0; i < count; i++) { nint remaining = Marshal.ReadIntPtr(array, i * nint.Size); if (remaining != 0) new Com(remaining).Dispose(); } Marshal.FreeCoTaskMem(array); }
        return devices;
    }
    public static Com Open(CaptureDevice device)
    {
        using var attrs = Native.Attributes(); attrs.Guid(Keys.SourceType, device.Audio ? Keys.AudioSource : Keys.VideoSource);
        attrs.String(device.Audio ? Keys.AudioLink : Keys.VideoLink, device.Id); return Native.Source(attrs);
    }
    public static List<VideoMode> Modes(CaptureDevice device)
    {
        using var usage = MediaFoundationUsage.Enter(); using var apartment = new Apartment(); using var source = Open(device);
        try
        {
            using var attrs = Native.Attributes(); attrs.U32(Keys.ReaderDoesNotShutdownSource, 1); using var reader = Native.Reader(source, attrs);
            var result = new List<VideoMode>();
            for (uint i = 0; i < 1024; i++)
            {
                using var type = Native.MediaType(reader, Keys.VideoStream, i); if (type == null) break;
                var (w, h) = Keys.Unpair(type.U64(Keys.Size)); var (n, d) = Keys.Unpair(type.U64(Keys.Rate));
                if (w > 0 && h > 0 && w <= 7680 && h <= 4320 && n > 0 && d > 0)
                    result.Add(new(i, w, h, n, d, type.Guid(Keys.Subtype)));
            }
            return result.OrderByDescending(x => x.Width * x.Height).ThenByDescending(x => x.Fps).ToList();
        }
        finally { source.Call(12); }
    }
}

internal sealed class CaptureStream
{
    public event Action<Packet>? Data;
    public event Action<string>? Progress;
    public event Action<Exception>? Failed;
    readonly object gate = new();
    readonly SharedOperation stop = new();
    readonly CancellationTokenSource cancel = new();
    readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Func<ICaptureBackend> createBackend;
    readonly TimeSpan shutdownTimeout = TimeSpan.FromSeconds(2);
    ICaptureBackend? backend;
    Task? worker;
    volatile bool stopping;
    Exception? cleanupError;
    static int unresponsive;
    public static bool HasUnresponsiveDevice => Volatile.Read(ref unresponsive) != 0;
    public PixelLayout Layout { get; private set; }
    public int AudioRate { get; private set; }
    public int AudioChannels { get; private set; }
    public CaptureStream(CaptureDevice device, VideoMode? mode = null, bool nativeVideo = false)
    {
        createBackend = () => new MediaCaptureBackend(device, mode, nativeVideo, step => Progress?.Invoke(step));
    }

    internal CaptureStream(Func<ICaptureBackend>? factory, TimeSpan timeout)
    {
        createBackend = factory ?? (() => throw new InvalidOperationException("No capture backend configured."));
        shutdownTimeout = timeout;
    }

    public Task Start()
    {
        lock (gate)
        {
            if (stopping) return Task.FromCanceled(new CancellationToken(true));
            if (worker == null) worker = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            return ready.Task;
        }
    }
    void Run()
    {
        IDisposable? usage = null;
        Apartment? apartment = null;
        ICaptureBackend? owned = null;
        try
        {
            usage = MediaFoundationUsage.Enter();
            using var scheduling = MultimediaScheduling.Enter("Capture");
            apartment = new Apartment();
            owned = createBackend();
            lock (gate) backend = owned;

            owned.Open(cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            Layout = owned.Layout;
            AudioRate = owned.AudioRate;
            AudioChannels = owned.AudioChannels;
            ready.TrySetResult();

            while (!stopping)
            {
                using var packet = owned.Read();
                if (stopping) break;
                if (packet == null) continue;

                Layout = packet.Layout;
                Data?.Invoke(packet);
            }
        }
        catch (OperationCanceledException) when (stopping) { ready.TrySetCanceled(); }
        catch (Exception ex) { ready.TrySetException(ex); if (!stopping) { try { Failed?.Invoke(ex); } catch { } } }
        finally
        {
            try { owned?.Dispose(); } catch (Exception ex) { cleanupError = ex; }
            lock (gate) { stopping = true; backend = null; cancel.Dispose(); }
            apartment?.Dispose();
            usage?.Dispose();
        }
    }
    internal Task Completion { get { lock (gate) return worker ?? Task.CompletedTask; } }
    public Task Stop() => stop.Run(StopCore);
    async Task StopCore()
    {
        Task running;
        ICaptureBackend? owned;
        lock (gate)
        {
            if (!stopping)
            {
                stopping = true;
                cancel.Cancel();
            }
            ready.TrySetCanceled();
            owned = backend;
            running = worker ?? Task.CompletedTask;
            if (worker == null) cancel.Dispose();
        }
        Task completion = owned?.RequiresExternalShutdown == true
            ? Task.WhenAll(running, Task.Run(() => { using var apartment = new Apartment(); owned.Shutdown(); }))
            : running;
        try { await completion.WaitAsync(shutdownTimeout).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            Interlocked.Exchange(ref unresponsive, 1);
            // Observe a late native failure while leaving pointers owned by the worker.
            _ = completion.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            throw new InvalidOperationException("The capture driver did not release its input. Close and reopen Xeno Capture before reconnecting.");
        }
        if (cleanupError != null) throw new InvalidOperationException("Capture cleanup failed.", cleanupError);
    }
}
internal static class Clock { public static long Now => (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency)); }













