using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace XenoCapture.Engine;
internal static class SchedulingDiagnostics
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeWaitHandle CreateWaitableTimerEx(nint attributes, string? name, uint flags, uint access);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period, nint callback, nint argument, [MarshalAs(UnmanagedType.Bool)] bool resume);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
    public static Dictionary<string, object> Run()
    {
        using var process = Process.GetCurrentProcess(); nint originalAffinity = process.ProcessorAffinity;
        long allowed = originalAffinity.ToInt64(), oneCore = allowed & -allowed;
        using var cancel = new CancellationTokenSource(); using var started = new CountdownEvent(3);
        var competitors = new List<Thread>(); var cases = new List<Dictionary<string, object>>();
        try
        {
            // Restrict this diagnostic process only; never touch another app.
            // Bounded workers contend on one core, leaving other cores free.
            process.ProcessorAffinity = (nint)oneCore;
            for (int i = 0; i < 3; i++)
            {
                var competitor = new Thread(() => { started.Signal(); while (!cancel.IsCancellationRequested) Thread.SpinWait(512); })
                { IsBackground = true, Name = "Xeno bounded scheduling test", Priority = ThreadPriority.Normal };
                competitors.Add(competitor); competitor.Start();
            }
            if (!started.Wait(3000)) throw new TimeoutException("Scheduling test workers did not start.");
            foreach (string task in new[] { "Capture", "Playback" })
            foreach (bool registered in new[] { false, true })
            {
                Exception? error = null; Dictionary<string, object>? result = null;
                var worker = new Thread(() => { try { result = Measure(task, registered); } catch (Exception ex) { error = ex; } })
                { IsBackground = true, Name = "Xeno scheduling measurement", Priority = task == "Playback" ? ThreadPriority.AboveNormal : ThreadPriority.Normal };
                worker.Start(); if (!worker.Join(15000)) throw new TimeoutException("Scheduling measurement did not finish.");
                if (error != null) throw error; cases.Add(result!);
            }
        }
        finally
        {
            cancel.Cancel(); foreach (var competitor in competitors) competitor.Join();
            process.ProcessorAffinity = originalAffinity;
        }
        // Unknown/disabled profiles must remain a benign fallback.
        using (var missing = MultimediaScheduling.Enter("Xeno nonexistent diagnostic task"))
            if (missing.Registered) throw new InvalidOperationException("An unsupported MMCSS profile unexpectedly registered.");
        if (MultimediaScheduling.ActiveRegistrations != 0 || MultimediaScheduling.FailedReverts != 0) throw new InvalidOperationException("A scheduling scope did not revert on its owner thread.");
        return new() { ["cases"] = cases, ["contention"] = "three normal-priority CPU workers sharing one core in this process only",
            ["affinityRestored"] = process.ProcessorAffinity == originalAffinity, ["activeRegistrationsAfterTest"] = MultimediaScheduling.ActiveRegistrations,
            ["failedReverts"] = MultimediaScheduling.FailedReverts, ["missingTaskFallback"] = "pass", ["measuresGameplayLatency"] = false, ["result"] = "PASS" };
    }
    internal static Dictionary<string, object> CheckLifecycle()
    {
        var checks = new Dictionary<string, object>();
        foreach (string task in new[] { "Capture", "Playback", "Audio" })
        {
            bool registered = false; Exception? error = null;
            var worker = new Thread(() =>
            {
                try
                {
                    var scope = MultimediaScheduling.Enter(task);
                    try { registered = scope.Registered; }
                    finally { scope.Dispose(); scope.Dispose(); }
                }
                catch (Exception ex) { error = ex; }
            }) { IsBackground = true };
            worker.Start(); if (!worker.Join(3000)) throw new TimeoutException("MMCSS lifecycle check did not complete.");
            if (error != null) throw error; checks["mmcss" + task + "Registered"] = registered;
        }
        using (var missing = MultimediaScheduling.Enter("Xeno nonexistent diagnostic task"))
            if (missing.Registered) throw new InvalidOperationException("MMCSS fallback unexpectedly registered.");
        if (MultimediaScheduling.ActiveRegistrations != 0 || MultimediaScheduling.FailedReverts != 0) throw new InvalidOperationException("MMCSS cleanup did not finish on the owner thread.");
        checks["mmcssMissingTaskFallback"] = "pass"; checks["mmcssOwnerThreadAndRepeatedCleanup"] = "pass";
        checks["mmcssRegistrationsAfterShutdown"] = MultimediaScheduling.ActiveRegistrations; checks["mmcssFailedReverts"] = MultimediaScheduling.FailedReverts;
        return checks;
    }
    static Dictionary<string, object> Measure(string task, bool enable)
    {
        using var scheduling = enable ? MultimediaScheduling.Enter(task) : null;
        using var timer = CreateWaitableTimerEx(0, null, 2, 0x100002);
        if (timer.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var overshoots = new List<double>();
        for (int i = 0; i < 140; i++)
        {
            long due = -166667; long before = Stopwatch.GetTimestamp();
            if (!SetWaitableTimer(timer, ref due, 0, 0, 0, false) || WaitForSingleObject(timer, 2000) != 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (i >= 20) overshoots.Add(Math.Max(0, Stopwatch.GetElapsedTime(before).TotalMilliseconds - 16.6667));
        }
        overshoots.Sort();
        return new() { ["task"] = task, ["mmcssRequested"] = enable, ["mmcssRegistered"] = scheduling?.Registered ?? false,
            ["wakeOvershootMedianMs"] = overshoots[overshoots.Count / 2], ["wakeOvershootP95Ms"] = overshoots[(int)((overshoots.Count - 1) * .95)],
            ["wakeOvershootP99Ms"] = overshoots[(int)((overshoots.Count - 1) * .99)], ["wakesOver4msLate"] = overshoots.Count(x => x > 4), ["samples"] = overshoots.Count };
    }
}
