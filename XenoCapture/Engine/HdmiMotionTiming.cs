using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using XenoCapture.UI;

namespace XenoCapture.Engine;

internal sealed class HdmiMotionTiming
{
    const int Columns = 32, Rows = 18;
    readonly object gate = new();
    readonly int[] previous = new int[Columns * Rows];
    readonly Dictionary<long, long> arrivals = new();
    readonly List<double> inputIntervals = new(), sourceIntervals = new(), sourceAge = new(),
        sourcePresentationIntervals = new(), outputIntervals = new();
    long lastInput, lastSourceTime, lastSourcePresentation, lastOutput;
    int captures, motionFrames, lowChangeFrames, transitions, presentations, generated;
    bool running;

    internal void Start() { lock (gate) running = true; }

    internal void Capture(Packet frame)
    {
        long now = Clock.Now;
        lock (gate)
        {
            if (!running) return;
            if (captures > 0)
            {
                inputIntervals.Add((now - lastInput) / 10000.0);
                sourceIntervals.Add((frame.Time - lastSourceTime) / 10000.0);
            }
            lastInput = now;
            lastSourceTime = frame.Time;
            arrivals[frame.Time] = now;

            int changed = 0, sample = 0;
            for (int y = 0; y < Rows; y++)
            {
                int row = (2 * y + 1) * frame.Height / (2 * Rows) * frame.Width;
                for (int x = 0; x < Columns; x++)
                {
                    int pixel = row + (2 * x + 1) * frame.Width / (2 * Columns);
                    int luma = frame.Layout == PixelLayout.Nv12 ? frame.Bytes[pixel] : RgbLuma(frame.Bytes, 4 * pixel);
                    if (Math.Abs(luma - previous[sample]) > 4) changed++;
                    previous[sample++] = luma;
                }
            }
            if (captures++ > 0)
            {
                transitions++;
                if (changed <= 2) lowChangeFrames++;
                if (changed >= Columns * Rows / 12) motionFrames++;
            }
        }
    }

    static int RgbLuma(byte[] bytes, int pixel) =>
        (19 * bytes[pixel] + 183 * bytes[pixel + 1] + 54 * bytes[pixel + 2]) >> 8;

    internal void Presented(PresentedFrame frame)
    {
        long now = Clock.Now;
        lock (gate)
        {
            if (!running) return;
            if (lastOutput != 0) outputIntervals.Add((now - lastOutput) / 10000.0);
            lastOutput = now;
            if (frame.Kind != PresentationKind.Source)
            {
                if (frame.Kind is PresentationKind.Predicted or PresentationKind.Interpolated) generated++;
                return;
            }

            presentations++;
            if (lastSourcePresentation != 0)
                sourcePresentationIntervals.Add((now - lastSourcePresentation) / 10000.0);
            lastSourcePresentation = now;
            if (arrivals.TryGetValue(frame.Time, out long arrival))
                sourceAge.Add(Math.Max(0, (now - arrival) / 10000.0));
        }
    }

    internal Dictionary<string, object> Finish(Direct3DHost host, PixelLayout? layout, Rectangle? display)
    {
        lock (gate)
        {
            running = false;
            return new()
            {
                ["videoLayout"] = layout?.ToString() ?? "Unknown",
                ["fullscreen"] = display.HasValue,
                ["displayWidth"] = display?.Width ?? 0,
                ["displayHeight"] = display?.Height ?? 0,
                ["displayHz"] = DisplayTiming.RefreshRate(host.Handle),
                ["aiVideo"] = host.AiVideoStatus,
                ["frameGeneration"] = host.FrameGeneration.ToString(),
                ["captures"] = captures,
                ["changedImageIntervals"] = transitions,
                ["motionFrames"] = motionFrames,
                ["nearStaticFrames"] = lowChangeFrames,
                ["sourcePresentations"] = presentations,
                ["generatedPresentations"] = generated,
                ["frameWaitTimeouts"] = host.FrameWaitTimeouts,
                ["inputArrival"] = Summarize(inputIntervals),
                ["deviceTimestamps"] = Summarize(sourceIntervals),
                ["sourcePresentationsInterval"] = Summarize(sourcePresentationIntervals),
                ["allPresentationsInterval"] = Summarize(outputIntervals),
                ["arrivalToPresent"] = Summarize(sourceAge)
            };
        }
    }

    static object Summarize(List<double> samples)
    {
        samples.Sort();
        double Percentile(double value) => samples.Count == 0 ? 0 : samples[(int)((samples.Count - 1) * value)];
        return new { samples = samples.Count, medianMs = Percentile(.5), p95Ms = Percentile(.95),
            p99Ms = Percentile(.99), maximumMs = Percentile(1), gapsOver25Ms = samples.Count(value => value > 25) };
    }
}
