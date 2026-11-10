using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace XenoCapture.Engine;

internal sealed record RecordingOptions(string Path, int SourceWidth, int SourceHeight, int Width, int Height, int RateNumerator, int RateDenominator, int Bitrate, int AudioRate, int AudioChannels, PixelLayout Layout = PixelLayout.Bgr32);
internal sealed class Recorder
{
    readonly RecordingOptions options;
    readonly Queue<Packet> video = new(), audio = new();
    readonly AutoResetEvent signal = new(false);
    readonly object queueLock = new();
    readonly long origin;
    bool stopping;
    Exception? failure;
    Task? worker;
    readonly SharedOperation stop = new();
    readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    long videoSamples;
    public long VideoSamplesWritten => Interlocked.Read(ref videoSamples);
    public long DroppedFrames { get; private set; }
    public event Action<Exception>? Failed;
    public Recorder(RecordingOptions options, long? origin = null) { this.options = options; this.origin = origin ?? Clock.Now; }
    public Task Start()
    {
        lock (queueLock)
        {
            if (stopping) return Task.FromCanceled(new CancellationToken(true));
            worker ??= Task.Factory.StartNew(() => Run(ready), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            return ready.Task;
        }
    }

    public void Submit(Packet packet)
    {
        bool audioBacklog;
        lock (queueLock)
        {
            if (stopping || packet.Time + packet.Duration < origin) return;
            var queue = packet.Audio ? audio : video;
            if (!packet.Audio && queue.Count >= 3) { DroppedFrames++; return; }

            audioBacklog = packet.Audio && queue.Count >= 128;
            if (audioBacklog) stopping = true;
            else queue.Enqueue(packet.Retain());
            signal.Set();
        }

        if (audioBacklog)
            NotifyFailure(new IOException("Recording stopped: the encoder cannot keep up with the audio input. Try a smaller output size."));
    }

    void NotifyFailure(Exception error)
    {
        try { Failed?.Invoke(error); }
        catch (Exception observerError)
        { System.Diagnostics.Trace.TraceWarning("A recording failure observer threw ({0}).", observerError.GetType().Name); }
    }

    void Run(TaskCompletionSource ready)
    {
        try
        {
            using var usage = MediaFoundationUsage.Enter();
            using var apartment = new Apartment();
            using var attrs = Native.Attributes(); attrs.U32(Keys.Hardware, 1); attrs.U32(Keys.LowLatency, 1); attrs.U32(Keys.NoThrottle, 1);
            using var outputStream = Native.CreateRecordingStream(options.Path);
            using var writer = Native.Writer(options.Path, outputStream, attrs);
            using var output = VideoType(Keys.H264, options.Width, options.Height); output.U32(Keys.Bitrate, (uint)options.Bitrate); output.U32(Keys.Profile, 100);
            uint vs = Native.AddStream(writer, output);
            bool nativeNv12 = options.Layout == PixelLayout.Nv12 && options.Width == options.SourceWidth && options.Height == options.SourceHeight;
            using var input = VideoType(nativeNv12 ? Keys.Nv12 : Keys.Rgb32, options.Width, options.Height);
            input.U32(Keys.Stride, (uint)(options.Width * (nativeNv12 ? 1 : 4))); Native.SetInput(writer, vs, input);
            uint aus = uint.MaxValue;
            if (options.AudioRate > 0)
            {
                using var aac = AudioType(true); aus = Native.AddStream(writer, aac);
                using var pcm = AudioType(false); Native.SetInput(writer, aus, pcm);
            }
            Native.Hr(writer.Call(5)); ready.TrySetResult();
            using var scaler = options.Width != options.SourceWidth || options.Height != options.SourceHeight ? new VideoScaler(options) : null;
            long lastVideo = -1, lastAudio = -1;
            while (true)
            {
                Packet? packet;
                lock (queueLock)
                {
                    // Merge by capture time while the queue heads are stable.
                    if (audio.TryPeek(out var nextAudio)
                        && (!video.TryPeek(out var nextVideo) || nextAudio.Time <= nextVideo.Time))
                        packet = audio.Dequeue();
                    else if (!video.TryDequeue(out packet) && stopping)
                        break;
                }
                if (packet != null)
                {
                    using (packet)
                    {
                        if (packet.Audio && aus == uint.MaxValue) continue;
                        long sampleTime = Math.Max(0, packet.Time - origin);
                        if (packet.Audio)
                        {
                            sampleTime = Math.Max(sampleTime, lastAudio + 1);
                            lastAudio = sampleTime;
                        }
                        else
                        {
                            sampleTime = Math.Max(sampleTime, lastVideo + 1);
                            lastVideo = sampleTime;
                        }
                        using var converted = !packet.Audio && scaler != null && packet.Layout == PixelLayout.Nv12
                            ? PixelConversion.ToBgra(packet) : null;
                        var bytes = packet.Audio || scaler == null
                            ? packet.Bytes.AsSpan(0, packet.Length) : scaler.Resize(converted ?? packet);
                        using var sample = Native.Sample(bytes, sampleTime, packet.Duration);
                        Native.Write(writer, packet.Audio ? aus : vs, sample);
                        if (!packet.Audio) Interlocked.Increment(ref videoSamples);
                    }
                    continue;
                }
                signal.WaitOne();
            }
            Native.Hr(writer.Call(11));
        }
        catch (Exception error)
        {
            failure = error;
            ready.TrySetException(error);
            NotifyFailure(error);
        }
        finally
        {
            lock (queueLock)
            {
                stopping = true;
                while (video.TryDequeue(out var frame)) frame.Dispose();
                while (audio.TryDequeue(out var sample)) sample.Dispose();
                signal.Dispose();
            }
        }
    }
    Com VideoType(Guid subtype, int width, int height)
    {
        var type = Native.Type();
        type.Guid(Keys.Major, Keys.Video); type.Guid(Keys.Subtype, subtype); type.U64(Keys.Size, Keys.Pair(width, height)); type.U64(Keys.Rate, Keys.Pair(options.RateNumerator, options.RateDenominator)); type.U64(Keys.Aspect, Keys.Pair(1, 1)); type.U32(Keys.Interlace, 2); return type;
    }
    Com AudioType(bool aac)
    {
        var type = Native.Type(); type.Guid(Keys.Major, Keys.Audio); type.Guid(Keys.Subtype, aac ? Keys.Aac : Keys.Pcm);
        type.U32(Keys.Channels, (uint)options.AudioChannels); type.U32(Keys.SampleRate, (uint)options.AudioRate); type.U32(Keys.Bits, 16);
        type.U32(Keys.BytesPerSecond, aac ? 24000u : (uint)(options.AudioRate * options.AudioChannels * 2));
        if (!aac) type.U32(Keys.BlockAlign, (uint)(options.AudioChannels * 2)); return type;
    }
    public Task Stop() => stop.Run(StopCore);
    async Task StopCore()
    {
        Task running;
        lock (queueLock)
        {
            bool wakeWorker = !stopping;
            stopping = true;
            ready.TrySetCanceled();
            running = worker ?? Task.CompletedTask;
            if (worker == null) signal.Dispose();
            else if (wakeWorker) signal.Set();
        }
        try { await running.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            _ = running.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            throw new IOException("The encoder did not finish shutdown. The recording may be incomplete.");
        }
        if (failure != null) throw new IOException("The MP4 encoder failed; the file may be incomplete.", failure);
    }
}
internal sealed class VideoScaler : IDisposable
{
    readonly Bitmap output;
    readonly Graphics graphics;
    readonly ImageAttributes attributes = new();
    readonly byte[] result;
    readonly RecordingOptions options;
    public VideoScaler(RecordingOptions options)
    {
        this.options = options; output = new(options.Width, options.Height, PixelFormat.Format32bppRgb); graphics = Graphics.FromImage(output);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; graphics.PixelOffsetMode = PixelOffsetMode.HighQuality; graphics.CompositingMode = CompositingMode.SourceCopy;
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        result = new byte[checked(options.Width * options.Height * 4)];
    }
    public unsafe ReadOnlySpan<byte> Resize(Packet packet)
    {
        fixed (byte* source = packet.Bytes)
        {
            using var input = new Bitmap(packet.Width, packet.Height, packet.Width * 4, PixelFormat.Format32bppRgb, (nint)source);
            // Letterbox instead of stretching a 4:3 or ultrawide input into a 16:9 output.
            graphics.Clear(Color.Black);
            double scale = Math.Min((double)options.Width / packet.Width, (double)options.Height / packet.Height);
            int w = (int)Math.Round(packet.Width * scale), h = (int)Math.Round(packet.Height * scale);
            graphics.DrawImage(input, new Rectangle((options.Width - w) / 2, (options.Height - h) / 2, w, h), 0, 0, packet.Width, packet.Height, GraphicsUnit.Pixel, attributes);
        }
        var locked = output.LockBits(new Rectangle(0, 0, output.Width, output.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            int rowBytes = output.Width * 4;
            if (locked.Stride == rowBytes)
                Marshal.Copy(locked.Scan0, result, 0, result.Length);
            else
                for (int y = 0; y < output.Height; y++)
                    Marshal.Copy(locked.Scan0 + y * locked.Stride, result, y * rowBytes, rowBytes);
        }
        finally { output.UnlockBits(locked); }
        return result;
    }
    public void Dispose()
    {
        attributes.Dispose();
        graphics.Dispose();
        output.Dispose();
    }
}




