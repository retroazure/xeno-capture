using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace XenoCapture.Engine;
internal enum FrameGenerationMode { Off, Adaptive2x, Adaptive3x, Adaptive4x }
internal enum PresentationKind { Source, Interpolated, Repeated }
internal enum FrameSlotWait { Ready, Interrupted, TimedOut }
internal readonly record struct PresentedFrame(long Time, long Duration, PresentationKind Kind = PresentationKind.Source);
internal static class FrameGenerationPolicy
{
    internal static int Multiplier(FrameGenerationMode mode) => mode switch
    { FrameGenerationMode.Adaptive2x => 2, FrameGenerationMode.Adaptive3x => 3, FrameGenerationMode.Adaptive4x => 4, _ => 1 };
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
    internal bool PreferThreePhases => consecutiveShortWindows >= 2;

    internal static bool Eligible(long period) => period is >= 80_000 and <= 190_000;

    internal void Observe(long now, long period)
    {
        if (PreferThreePhases) return;
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
