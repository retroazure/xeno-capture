using System;
using System.Threading;
using System.Threading.Tasks;

namespace XenoCapture.Engine;
internal sealed class TestSignal
{
    readonly CancellationTokenSource cancel = new();
    Task? worker;
    readonly object gate = new();
    readonly SharedOperation stop = new();
    bool stopping;
    public event Action<Packet>? Data;
    public void Start()
    {
        lock (gate)
        {
        if (stopping) throw new ObjectDisposedException(nameof(TestSignal)); if (worker != null) return;
        worker = Task.Run(async () =>
        {
            long start = Clock.Now; int frame = 0;
            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    long t = start + frame * 10_000_000L / 30;
                    using var video = Frame(frame, 1280, 720); video.Time = t; video.Duration = 10_000_000 / 30; Data?.Invoke(video);
                    using var audio = Tone(frame * 1600, 1600); audio.Time = t; audio.Duration = 10_000_000 / 30; Data?.Invoke(audio);
                    frame++;
                    int wait = (int)((start + frame * 10_000_000L / 30 - Clock.Now) / 10000);
                    await Task.Delay(Math.Max(1, wait), cancel.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
        }
    }
    public static Packet Frame(int frame, int width, int height)
    {
        var packet = new Packet(width * height * 4, false, width, height); var bytes = packet.Bytes;
        int[] colors = [0xDBE4CF, 0xE6D86D, 0x63D5C6, 0x63ACD5, 0x8580CF, 0xCB7E9A, 0xCB725D, 0x222A32];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int c = colors[Math.Min(7, x * 8 / width)]; double fade = 0.3 + 0.7 * (1.0 - (double)y / height);
            if (y > height * 0.7) { c = ((x / 12 + y / 12) % 2 == 0) ? 0xE4E9DE : 0x151C24; fade = 1; }
            if (Math.Abs(x - ((frame * 9) % width)) < 3) { c = 0xFFFFFF; fade = 1; }
            int i = (y * width + x) * 4; bytes[i] = (byte)((c & 255) * fade); bytes[i + 1] = (byte)(((c >> 8) & 255) * fade); bytes[i + 2] = (byte)(((c >> 16) & 255) * fade); bytes[i + 3] = 255;
        }
        return packet;
    }
    public static Packet Tone(int offset, int frames)
    {
        var packet = new Packet(frames * 4, true);
        for (int n = 0; n < frames; n++)
        {
            short sample = (short)(Math.Sin((offset + n) * 2 * Math.PI * 440 / 48000) * 1800);
            int i = n * 4; packet.Bytes[i] = packet.Bytes[i + 2] = (byte)(sample & 255); packet.Bytes[i + 1] = packet.Bytes[i + 3] = (byte)(sample >> 8);
        }
        return packet;
    }
    public Task Stop() => stop.Run(async () => { Task running; lock (gate) { stopping = true; cancel.Cancel(); running = worker ?? Task.CompletedTask; } try { await running; } finally { cancel.Dispose(); } });
}

