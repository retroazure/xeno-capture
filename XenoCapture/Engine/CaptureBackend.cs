using System;
using System.Runtime.InteropServices;
using System.Threading;


namespace XenoCapture.Engine;
internal interface ICaptureBackend : IDisposable
{
    PixelLayout Layout { get; }
    int AudioRate { get; }
    int AudioChannels { get; }
    void Open(CancellationToken cancellation);
    Packet? Read();
    bool RequiresExternalShutdown => true;
    void Shutdown();
}

// The capture worker owns all native reader and source operations.
// Cancellation wakes its managed wait even when the device has no HDMI signal.
internal sealed class MediaCaptureBackend(CaptureDevice device, VideoMode? mode, bool nativeVideo, Action<string> progress) : ICaptureBackend
{
    sealed class NativeVideoBufferException() : InvalidOperationException("The capture driver returned an incompatible NV12 buffer.");
    const uint ReadError = 0x1, EndOfStream = 0x2, MediaTypeChanged = 0x30;
    Com? source, reader;
    SourceReaderCallback? callback;
    int disposed, width, height, stride;
    bool fullRange, bt601;
    long? firstTimestamp;
    long origin;
    CancellationToken captureCancellation;
    uint Stream => device.Audio ? Keys.AudioStream : Keys.VideoStream;
    public PixelLayout Layout { get; private set; }
    public int AudioRate { get; private set; }
    public int AudioChannels { get; private set; }
    public void Open(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested(); progress("opening device");
        var opened = CaptureDevices.Open(device);
        source = opened;
        captureCancellation = cancellation;
        bool useNative = nativeVideo && !device.Audio && mode?.Subtype == Keys.Nv12;
        try { ConfigureReader(opened, useNative); }
        catch (Exception error) when (useNative && !cancellation.IsCancellationRequested
            && error is COMException or ArgumentException or InvalidOperationException)
        {
            UseRgbReader(opened);
        }
        progress("capture ready");
    }
    void UseRgbReader(Com opened)
    {
        captureCancellation.ThrowIfCancellationRequested();
        CloseReader();
        ConfigureReader(opened, false);
    }
    void ConfigureReader(Com opened, bool useNative)
    {
        captureCancellation.ThrowIfCancellationRequested();
        using var attrs = Native.Attributes(); attrs.U32(Keys.LowLatency, 1); attrs.U32(Keys.ReaderDoesNotShutdownSource, 1);
        callback = new SourceReaderCallback(captureCancellation);
        callback.Attach(attrs);
        Layout = useNative ? PixelLayout.Nv12 : PixelLayout.Bgr32;
        if (!device.Audio && Layout != PixelLayout.Nv12) { attrs.U32(Keys.AdvancedProcessing, 1); attrs.U32(Keys.Hardware, 1); }
        progress("creating source reader"); reader = Native.Reader(opened, attrs);
        captureCancellation.ThrowIfCancellationRequested();
        Native.Select(reader, Keys.AllStreams, false); Native.Select(reader, Stream, true);
        if (!device.Audio && mode != null)
        {
            using var selected = Native.MediaType(reader, Stream, mode.Index) ?? throw new InvalidOperationException("This capture format is no longer available.");
            progress("selecting native format"); Native.SetType(reader, Stream, selected);
        }
        using var requested = Native.Type(); requested.Guid(Keys.Major, device.Audio ? Keys.Audio : Keys.Video);
        requested.Guid(Keys.Subtype, device.Audio ? Keys.Pcm : Layout == PixelLayout.Nv12 ? Keys.Nv12 : Keys.Rgb32);
        if (device.Audio) requested.U32(Keys.Bits, 16);
        progress("setting output format"); if (Layout != PixelLayout.Nv12 || device.Audio) Native.SetType(reader, Stream, requested);
        UpdateMediaFormat(reader, false);
        captureCancellation.ThrowIfCancellationRequested();
    }
    void UpdateMediaFormat(Com input, bool refresh)
    {
        using var actual = Native.MediaType(input, Stream, 0, true) ?? throw new InvalidOperationException("No media format was negotiated.");
        var subtype = actual.Guid(Keys.Subtype);
        if (device.Audio)
        {
            int rate = (int)actual.U32(Keys.SampleRate), channels = (int)actual.U32(Keys.Channels);
            if (subtype != Keys.Pcm || actual.U32(Keys.Bits) != 16 || channels is < 1 or > 2 || rate is not (44100 or 48000))
                throw new InvalidOperationException("Select a 44.1 or 48 kHz mono/stereo input in Windows sound settings.");
            if (refresh && (rate != AudioRate || channels != AudioChannels))
                throw new InvalidOperationException("The audio input changed format. Reconnect to apply its new format.");
            AudioRate = rate;
            AudioChannels = channels;
        }
        else
        {
            var (w, h) = Keys.Unpair(actual.U64(Keys.Size));
            if (w < 1 || h < 1 || w > 7680 || h > 4320 || (Layout == PixelLayout.Nv12 && ((w | h) & 1) != 0))
                throw new InvalidOperationException("Invalid negotiated frame dimensions.");
            var expected = Layout == PixelLayout.Nv12 ? Keys.Nv12 : Keys.Rgb32;
            if (refresh && (w != width || h != height || subtype != expected))
                throw new InvalidOperationException("The HDMI input changed resolution or pixel format. Reconnect to select its new format.");
            int pitch = unchecked((int)actual.Get32(Keys.Stride, (uint)(w * (Layout == PixelLayout.Nv12 ? 1 : 4))));
            if (Layout == PixelLayout.Nv12 && (subtype != expected || pitch < w)) throw new NativeVideoBufferException();
            if (subtype != expected) throw new InvalidOperationException("The capture device returned an unsupported output format.");
            width = w;
            height = h;
            stride = pitch;
            fullRange = actual.U32(Keys.NominalRange) == 1;
            bt601 = actual.Get32(Keys.YuvMatrix, width < 1280 ? 2u : 1u) == 2;
        }
    }
    public Packet? Read()
    {
        try { return ReadPacket(); }
        catch (NativeVideoBufferException) when (firstTimestamp == null && Layout == PixelLayout.Nv12)
        {
            // Confirm the driver's first actual buffer before committing its
            // layout to the preview or a source-format recording.
            UseRgbReader(source ?? throw new ObjectDisposedException(nameof(MediaCaptureBackend)));
            return ReadPacket();
        }
    }
    unsafe Packet? ReadPacket()
    {
        var input = reader ?? throw new ObjectDisposedException(nameof(MediaCaptureBackend));
        using var sample = callback!.Read(input, Stream, out var flags, out var timestamp);
        if ((flags & (ReadError | EndOfStream)) != 0) throw new InvalidOperationException("The capture device disconnected or stopped sending data.");
        if ((flags & MediaTypeChanged) != 0) UpdateMediaFormat(input, true);
        if (sample == null) return null;
        nint bp; Native.Hr(((delegate* unmanaged[Stdcall]<nint, nint*, int>)sample.Slot(41))(sample.P, &bp)); using var buffer = new Com(bp);
        nint data; uint max, length; Native.Hr(((delegate* unmanaged[Stdcall]<nint, nint*, uint*, uint*, int>)buffer.Slot(3))(bp, &data, &max, &length));
        Packet? packet = null;
        try
        {
            if (length > max || (length != 0 && data == 0)) throw new InvalidOperationException("The capture driver returned an invalid buffer length.");
            if (device.Audio)
            {
                if (length == 0 || length > 4 * 1024 * 1024 || length % (AudioChannels * 2) != 0) return null;
                packet = new((int)length, true); Marshal.Copy(data, packet.Bytes, 0, (int)length);
                packet.Duration = (long)length * 10_000_000 / (AudioRate * AudioChannels * 2);
            }
            else
            {
                if (!VideoCopyLayout.TryCreate(Layout, width, height, stride, length, out var copy))
                {
                    if (Layout == PixelLayout.Nv12) throw new NativeVideoBufferException();
                    throw new InvalidOperationException("Invalid video buffer from the capture driver.");
                }
                packet = new(copy.Length, false, width, height, Layout) { FullRange = fullRange, Bt601 = bt601 };
                if (copy.Pitch == copy.RowBytes) Marshal.Copy(data, packet.Bytes, 0, copy.Length);
                else
                    for (int row = 0; row < copy.Rows; row++)
                        Marshal.Copy(checked(data + (nint)copy.RowOffset(row)), packet.Bytes, row * copy.RowBytes, copy.RowBytes);
                packet.Duration = mode == null ? 333333 : (long)(10_000_000 / mode.Fps);
            }
            if (firstTimestamp == null) { firstTimestamp = timestamp; origin = Clock.Now - packet.Duration; }
            try
            {
                packet.Time = checked(origin + (timestamp - firstTimestamp.Value));
                _ = checked(packet.Time + packet.Duration);
            }
            catch (OverflowException ex)
            {
                throw new InvalidOperationException("The capture driver returned a timestamp outside the supported range.", ex);
            }
            return packet;
        }
        catch { packet?.Dispose(); throw; }
        finally { buffer.Call(4); }
    }
    internal readonly record struct VideoCopyLayout(int RowBytes, int Rows, int Pitch)
    {
        internal int Length => RowBytes * Rows;

        internal long RowOffset(int row)
        {
            int physicalRow = Pitch < 0 ? Rows - 1 - row : row;
            return physicalRow * Math.Abs((long)Pitch);
        }

        internal static bool TryCreate(PixelLayout layout, int width, int height, int stride, uint length, out VideoCopyLayout copy)
        {
            copy = default;
            bool nv12 = layout == PixelLayout.Nv12;
            if (width is < 1 or > 7680 || height is < 1 or > 4320 || (nv12 && ((width | height) & 1) != 0))
                return false;

            int rowBytes = width * (nv12 ? 1 : 4), rows = nv12 ? height * 3 / 2 : height;
            // Lock can provide a tightly packed NV12 view despite a padded native stride.
            int pitch = nv12 && length == rowBytes * rows ? rowBytes : stride;
            long spacing = Math.Abs((long)pitch);
            if ((nv12 && pitch < rowBytes) || spacing < rowBytes || spacing * (rows - 1) + rowBytes > length)
                return false;

            copy = new(rowBytes, rows, pitch);
            return true;
        }
    }

    // Cancellation wakes Read. The worker flushes and releases the reader,
    // then shuts down the source without a second COM thread.
    public bool RequiresExternalShutdown => false;
    public void Shutdown() { }

    void CloseReader()
    {
        try
        {
            if (reader != null) callback?.FlushPending(reader, Stream);
        }
        finally
        {
            try { reader?.Dispose(); }
            finally { reader = null; callback?.Dispose(); callback = null; }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { CloseReader(); }
        finally
        {
            var owned = source;
            source = null;
            if (owned != null)
            {
                using (owned)
                {
                    int hr = owned.Call(12);
                    if (hr != unchecked((int)0xC00D3E85)) Native.Hr(hr); // MF_E_SHUTDOWN
                }
            }
        }
    }
}
