using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace XenoCapture.Engine;
// Owned by one synchronous worker thread, just like its native device/context.
// MMCSS is optional: disabled services/task profiles keep ordinary scheduling.
internal sealed class MultimediaScheduling : IDisposable
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    static extern nint Register(string task, ref uint index);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("avrt.dll", EntryPoint = "AvRevertMmThreadCharacteristics", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool Revert(nint handle);
    nint handle;
    readonly int owner = Environment.CurrentManagedThreadId;
    static int active, failedReverts;
    internal static int ActiveRegistrations => Volatile.Read(ref active);
    internal static int FailedReverts => Volatile.Read(ref failedReverts);
    internal bool Registered => handle != 0;
    MultimediaScheduling(nint handle) { this.handle = handle; if (handle != 0) Interlocked.Increment(ref active); }
    internal static MultimediaScheduling Enter(string task)
    {
        try
        {
            uint index = 0; var scope = new MultimediaScheduling(Register(task, ref index));
            return scope;
        }
        catch (DllNotFoundException) { return new(0); }
        catch (EntryPointNotFoundException) { return new(0); }
    }
    public void Dispose()
    {
        if (handle == 0) return;
        if (Environment.CurrentManagedThreadId != owner) throw new InvalidOperationException("Multimedia scheduling must be released by its worker thread.");
        nint owned = Interlocked.Exchange(ref handle, 0);
        if (owned == 0) return;
        if (!Revert(owned)) Interlocked.Increment(ref failedReverts);
        Interlocked.Decrement(ref active);
    }
}
