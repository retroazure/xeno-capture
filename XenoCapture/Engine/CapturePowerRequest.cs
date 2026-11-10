using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace XenoCapture.Engine;

internal sealed unsafe class CapturePowerRequest : IDisposable
{
    // Reserve REASON_CONTEXT's full union on the app's supported x64 platform.
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    struct ReasonContext
    {
        [FieldOffset(0)] public uint Version;
        [FieldOffset(4)] public uint Flags;
        [FieldOffset(8)] public nint SimpleReason;
    }

    enum RequestType { DisplayRequired, SystemRequired }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeFileHandle PowerCreateRequest(ref ReasonContext reason);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PowerSetRequest(SafeFileHandle request, RequestType type);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PowerClearRequest(SafeFileHandle request, RequestType type);

    SafeFileHandle? handle;
    static int activeRequests;
    internal static int ActiveRequests => Volatile.Read(ref activeRequests);
    internal bool Active => handle is { IsClosed: false, IsInvalid: false };

    internal CapturePowerRequest()
    {
        SafeFileHandle request;
        fixed (char* text = "Xeno Capture is displaying live video.")
        {
            var reason = new ReasonContext { Version = 0, Flags = 1, SimpleReason = (nint)text };
            request = PowerCreateRequest(ref reason);
        }

        try
        {
            if (request.IsInvalid || !PowerSetRequest(request, RequestType.SystemRequired)
                || !PowerSetRequest(request, RequestType.DisplayRequired))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not keep the capture display awake.");

            handle = request;
            Interlocked.Increment(ref activeRequests);
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        var owned = Interlocked.Exchange(ref handle, null);
        if (owned == null) return;
        PowerClearRequest(owned, RequestType.DisplayRequired);
        PowerClearRequest(owned, RequestType.SystemRequired);
        owned.Dispose();
        Interlocked.Decrement(ref activeRequests);
    }
}
