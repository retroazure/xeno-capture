using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace XenoCapture.UI;

internal enum InterfaceCue { Tap, Connected, Disconnected }

internal sealed class InterfaceSounds
{
    const int SampleRate = 24000;
    const uint Async = 0x1, NoDefault = 0x2, Memory = 0x4;
    readonly object gate = new();
    int generation;
    bool playing;

    // Keep the wave buffers rooted and pinned for asynchronous WinMM playback.
    static readonly Lazy<byte[]> tap = new(() => BuildWave(
        new Note(820, 660, 0, .054, .16), new Note(1180, 1080, .014, .054, .065)));
    static readonly Lazy<byte[]> connected = new(() => BuildWave(
        new Note(523, 555, 0, .125, .14), new Note(784, 813, .037, .132, .135),
        new Note(1047, 1047, .082, .125, .095)));
    static readonly Lazy<byte[]> disconnected = new(() => BuildWave(
        new Note(698, 650, 0, .093, .135), new Note(494, 440, .038, .122, .13)));

    internal void Play(InterfaceCue cue)
    {
        int request = Interlocked.Increment(ref generation);
        _ = Task.Run(() =>
        {
            lock (gate)
            {
                if (request != Volatile.Read(ref generation)) return;
                byte[] wave = Wave(cue);
                if (request == Volatile.Read(ref generation)) PlayWave(wave);
            }
        });
    }

    internal Task SilenceAsync()
    {
        Interlocked.Increment(ref generation);
        return Task.Run(() =>
        {
            lock (gate)
            {
                if (!playing) return;
                _ = PlaySound(0, 0, 0);
                playing = false;
            }
        });
    }

    unsafe void PlayWave(byte[] wave)
    {
        fixed (byte* data = wave)
            playing = PlaySound((nint)data, 0, Async | NoDefault | Memory) || playing;
    }

    internal static byte[] Wave(InterfaceCue cue) => cue switch
    {
        InterfaceCue.Connected => connected.Value,
        InterfaceCue.Disconnected => disconnected.Value,
        _ => tap.Value
    };

    readonly record struct Note(double Frequency, double EndFrequency, double Start, double Duration, double Amplitude);

    static byte[] BuildWave(params Note[] notes)
    {
        double duration = 0;
        foreach (var note in notes) duration = Math.Max(duration, note.Start + note.Duration);
        int samples = (int)Math.Ceiling(duration * SampleRate);
        int pcmBytes = samples * sizeof(short);
        byte[] wave = GC.AllocateUninitializedArray<byte>(44 + pcmBytes, pinned: true);
        using var stream = new MemoryStream(wave, writable: true);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + pcmBytes); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(SampleRate); writer.Write(SampleRate * sizeof(short));
        writer.Write((short)sizeof(short)); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(pcmBytes);

        for (int i = 0; i < samples; i++)
        {
            double value = 0;
            foreach (var note in notes)
            {
                double time = i / (double)SampleRate - note.Start;
                if (time < 0 || time >= note.Duration) continue;
                double progress = time / note.Duration;
                double phase = Math.Tau * time * (note.Frequency + .5 * (note.EndFrequency - note.Frequency) * progress);
                double fade = 1 - progress;
                double envelope = Math.Min(time / .006, 1) * fade * fade;
                double tone = .88 * Math.Sin(phase) + .09 * Math.Sin(phase * 2) + .03 * Math.Sin(phase * 3);
                value += tone * envelope * note.Amplitude;
            }
            writer.Write((short)(value * short.MaxValue));
        }
        return wave;
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PlaySound(nint sound, nint module, uint flags);
}
