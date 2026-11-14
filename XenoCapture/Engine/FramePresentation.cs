using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace XenoCapture.Engine;
internal enum FrameGenerationMode { Off, Adaptive2x, Adaptive3x, Adaptive4x, Predictive60, Predictive120, Predictive180, RecoverGaps }
internal enum PresentationKind { Source, Interpolated, Repeated, Predicted }
internal enum FrameSlotWait { Ready, Interrupted, TimedOut }
internal readonly record struct PresentedFrame(long Time, long Duration, PresentationKind Kind = PresentationKind.Source);
internal readonly record struct SourceFramePacing(long Time, long Duration, long Arrival, uint? PresentCount = null)
{
    internal static bool HasRefreshHeadroom(double refreshHz, long period) =>
        period is >= 80_000 and <= 500_000
        && refreshHz + Math.Max(1.1, refreshHz * .01) >= 20_000_000.0 / period;

    internal long Deadline(long time, long duration, long arrival, double refreshHz, bool useDisplayBudget = false)
    {
        if (Arrival == 0 || time <= Time || !HasRefreshHeadroom(refreshHz, duration)
            || Duration < duration - duration / 16 || Duration > duration + duration / 16)
            return arrival;

        long sourceGap = time - Time, arrivalGap = arrival - Arrival;
        // Use the capture clock as the cadence reference. Bypass discontinuities
        // and bursts that are too large for a short correction.
        if (sourceGap < duration - duration / 12 || sourceGap > duration + duration / 12
            || arrivalGap < sourceGap * 3 / 4 || arrivalGap > sourceGap - 7_500)
            return arrival;

        long maximumHold = useDisplayBudget ? MaximumDisplayHold(duration, 10_000_000 / refreshHz)
            : Math.Min(20_000, Math.Min(duration / 8, (long)(5_000_000 / refreshHz)));
        return arrival + Math.Min(sourceGap - arrivalGap, maximumHold);
    }

    internal long? DisplayDeadline(long time, long duration, long arrival, double refreshHz, long displayedAt,
        bool drawAhead = false)
    {
        long sourceGap = time - Time, arrivalGap = arrival - Arrival;
        if (!HasRefreshHeadroom(refreshHz, duration) || Math.Abs(Duration - duration) > duration / 16
            || sourceGap < duration - duration / 12 || sourceGap > duration + duration / 12
            || arrivalGap < sourceGap * 3 / 4)
            return null;

        double refreshPeriod = 10_000_000 / refreshHz;
        double slots = duration / refreshPeriod, wholeSlots = Math.Round(slots);
        if (wholeSlots < 2 || Math.Abs(slots - wholeSlots) > .08
            || displayedAt < Arrival || displayedAt - Arrival > 2 * refreshPeriod || displayedAt > arrival)
            return null;

        // Submit inside the intended refresh window. An already prepared image
        // can use a larger guard against uncertainty in DXGI's vblank timestamp.
        long guard = drawAhead ? (long)Math.Round(refreshPeriod / 4) : 3_500;
        long start = displayedAt + (long)Math.Round((wholeSlots - 1) * refreshPeriod) + guard;
        return start > arrival && start - arrival <= MaximumDisplayHold(duration, refreshPeriod) ? start : arrival;
    }

    static long MaximumDisplayHold(long duration, double refreshPeriod) =>
        Math.Min(60_000, Math.Min(duration / 3, (long)refreshPeriod));
}

internal struct FullscreenSourceCadence
{
    long previousTime, previousDuration, expectedArrival;
    bool previousArrivalWasLate;

    internal static uint RefreshInterval(long duration, double refreshHz)
    {
        if (!SourceFramePacing.HasRefreshHeadroom(refreshHz, duration)) return 0;
        double refreshes = duration * refreshHz / 10_000_000;
        double whole = Math.Round(refreshes);
        return whole is >= 2 and <= 4 && Math.Abs(refreshes - whole) <= .05 ? (uint)whole : 0;
    }

    internal static long MaximumHold(double refreshHz) => Math.Min(30_000, (long)(7_500_000 / refreshHz));

    internal long Deadline(long time, long duration, long arrival, double refreshHz)
    {
        long gap = time - previousTime;
        bool continuous = previousDuration != 0 && gap >= duration * 3 / 4 && gap <= duration * 5 / 4
            && Math.Abs(previousDuration - duration) <= duration / 32;
        previousTime = time;
        previousDuration = duration;
        long error = arrival - (expectedArrival + duration);
        if (!continuous || error < -duration / 2 || error > duration / 2)
        {
            // Show an isolated late frame immediately. Keep its original phase
            // so the next on-time capture can resume steady presentation.
            bool isolatedDelay = continuous && !previousArrivalWasLate
                && error > duration / 2 && error <= duration;
            expectedArrival = isolatedDelay ? expectedArrival + duration : arrival;
            previousArrivalWasLate = isolatedDelay;
            return arrival;
        }
        previousArrivalWasLate = false;

        // Track the later edge of normal capture jitter without adding a fixed
        // reserve to on-time frames. A late capture can always present immediately.
        expectedArrival += duration + (error > 0 ? error : error / 32);
        return arrival + Math.Clamp(expectedArrival - arrival, 0, MaximumHold(refreshHz));
    }
}

internal static class FrameGenerationPolicy
{
    internal static int Multiplier(FrameGenerationMode mode) => mode switch
    { FrameGenerationMode.Adaptive2x => 2, FrameGenerationMode.Adaptive3x => 3, FrameGenerationMode.Adaptive4x => 4, _ => 1 };
    internal static int PredictiveTarget(FrameGenerationMode mode) => mode switch
    { FrameGenerationMode.Predictive60 => 60, FrameGenerationMode.Predictive120 => 120, FrameGenerationMode.Predictive180 => 180, _ => 0 };
    internal static string WaitingStatus(FrameGenerationMode mode) => mode == FrameGenerationMode.Off ? "Off"
        : PredictiveTarget(mode) > 0 || mode == FrameGenerationMode.RecoverGaps
            ? "Waiting for video · no frame buffering" : "Waiting for video · adds one frame of delay";
    internal static long GapRecoveryGrace(long period) => Math.Min(15_000, period / 8);
    internal static long GapRecoveryExpiry(long period) => period + Math.Min(80_000, period / 2);
    internal static long PredictionInterval(int target, double refreshHz, long contentPeriod)
    {
        if (target <= 0 || refreshHz <= 1 || contentPeriod is < 80_000 or > 700_000) return 0;
        long interval = (long)Math.Round(10_000_000 / Math.Min(target, refreshHz));
        return contentPeriod >= interval + interval / 4 ? interval : 0;
    }
    internal static bool CanGenerate(long period, double refreshHz, int width, int height, int multiplier) =>
        multiplier is >= 2 and <= 4 && period >= 80_000 && period <= 700_000 && refreshHz > 1 && 10_000_000.0 * multiplier / period <= refreshHz + Math.Max(1.1, refreshHz * .01) && width > 0 && height > 0 && (long)width * height <= 1920L * 1080;
    internal static long Content30Period(long capturePeriod)
    {
        if (capturePeriod <= 0 || capturePeriod > 700_000) return 333333;
        int repeats = Math.Clamp((int)Math.Round(333333.0 / capturePeriod),1,8);
        long period = capturePeriod * repeats;
        return Math.Abs(period - 333333) <= 5000 ? period : 333333;
    }
    internal static long CadenceLead(long period, int multiplier, double analysisMs)
    {
        // Leave room for the measured work and a small wake-up margin. Bound
        // the delay even when GPU initialization was unusually expensive.
        long maximum = Math.Clamp(period / (2 * multiplier), 10_000, 40_000);
        return (long)Math.Clamp(analysisMs * 10_000 + 2_500, 10_000, maximum);
    }


    internal static int HighRateFallbackMultiplier(long period, double refreshHz, bool synchronizedFullscreen)
    {
        if (!synchronizedFullscreen) return 3;
        double refreshesPerCapture = period * refreshHz / 10_000_000;
        bool Even(int multiplier)
        {
            double refreshes = refreshesPerCapture / multiplier;
            return refreshes >= .98 && Math.Abs(refreshes - Math.Round(refreshes)) < .02;
        }

        // For example, 120 FPS fills two refreshes per frame on a fixed 240 Hz display.
        return Even(2) && !Even(3) ? 2 : 3;
    }

    internal static long GameCadenceCorrection(long desired, long due, long period)
    {
        long error = desired - due;
        return Math.Abs(error) > period / 2 ? 0 : Math.Clamp(error / 8, -2_000, 2_000);
    }
    internal static bool ShouldPresentPhase(PresentationKind kind, bool intermediate, long pair, long lastGeneratedPair)
        => kind != PresentationKind.Repeated || !intermediate || pair == 0 || pair != lastGeneratedPair;

    internal static long PhaseTime(long start, long period, int step, int multiplier) => start + period * step / multiplier;
    internal static int MissedPhases(long now, long due, long period, int multiplier)
    {
        long interval = period / multiplier;
        long late = now - due;
        return late <= interval * 9 / 10 ? 0 : (int)Math.Min(multiplier, (late + interval / 10) / interval);
    }
}
// Observe accepted presentations, since the monitor's advertised refresh
// does not guarantee that DXGI can sustain that rate on this output path.
internal struct HighRateCadence
{
    const long ObservationWindow = 5_000_000;
    long startedAt;
    int accepted, consecutiveShortWindows;
    internal bool NeedsLowerMultiplier => consecutiveShortWindows >= 2;

    internal static bool Eligible(long period) => period is >= 80_000 and <= 190_000;

    internal void Observe(long now, long period)
    {
        if (NeedsLowerMultiplier) return;
        if (startedAt == 0) { startedAt = now; return; }
        accepted++;
        long elapsed = now - startedAt;
        if (elapsed < ObservationWindow) return;

        consecutiveShortWindows = accepted * (double)period < elapsed * 3.6
            ? consecutiveShortWindows + 1 : 0;
        startedAt = now;
        accepted = 0;
    }
}

internal sealed unsafe class FrameDeadlineWaiter : IDisposable
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeWaitHandle CreateWaitableTimerEx(nint attributes, string? name, uint flags, uint access);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period, nint callback, nint argument, [MarshalAs(UnmanagedType.Bool)] bool resume);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForMultipleObjects(uint count, nint[] handles, [MarshalAs(UnmanagedType.Bool)] bool all, uint milliseconds);
    readonly SafeWaitHandle timer;
    readonly nint[] handles = new nint[2];
    public FrameDeadlineWaiter(AutoResetEvent signal)
    {
        timer = CreateWaitableTimerEx(0, null, 2, 0x100002);
        if (timer.IsInvalid) { int error = Marshal.GetLastWin32Error(); timer.Dispose(); throw new System.ComponentModel.Win32Exception(error); }
        handles[0] = signal.SafeWaitHandle.DangerousGetHandle(); handles[1] = timer.DangerousGetHandle();
    }
    public void WaitUntil(long deadline)
    {
        long due = Clock.Now - deadline;
        if (due >= 0) return;
        if (!SetWaitableTimer(timer, ref due, 0, 0, 0, false)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        uint result = WaitForMultipleObjects(2, handles, false, 20);
        if (result is not (0 or 1 or 258)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Dispose() => timer.Dispose();
}
internal static unsafe class DisplayTiming
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct MonitorInfo
    { public uint Size; public int Left, Top, Right, Bottom, WorkLeft, WorkTop, WorkRight, WorkBottom; public uint Flags; public fixed char Device[32]; }
    [StructLayout(LayoutKind.Explicit, Size = 220)] struct DisplayMode
    { [FieldOffset(68)] public ushort Size; [FieldOffset(184)] public uint Frequency; }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint window, uint flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetMonitorInfo(nint monitor, MonitorInfo* info);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode, ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool EnumDisplaySettings(string? device, int index, DisplayMode* mode);
    internal static double RefreshRateForDevice(string device)
    {
        var mode=new DisplayMode{Size=220};
        return EnumDisplaySettings(device,-1,&mode)&&mode.Frequency is >1 and <1000 ? mode.Frequency:0;
    }
    internal static double RefreshRate(nint window)
    {
        var info = new MonitorInfo { Size = (uint)sizeof(MonitorInfo) }; var mode = new DisplayMode { Size = 220 };
        if (!GetMonitorInfo(MonitorFromWindow(window, 2), &info)) return 0;
        return EnumDisplaySettings(new string(info.Device), -1, &mode) && mode.Frequency is > 1 and < 1000 ? mode.Frequency : 0;
    }
}
