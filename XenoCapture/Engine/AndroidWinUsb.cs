using System;
using System.ComponentModel;
using System.IO;

using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace XenoCapture.Engine;

// USB only: use the Windows driver registered for the Android debugging interface.
internal sealed unsafe class AndroidWinUsb : IAndroidUsb
{
    static readonly Guid AdbInterface = new("f72fe0d4-cbcb-407d-8814-9ed673d0dd6b");
    readonly SafeFileHandle file;
    readonly UsbHandle usb;
    readonly byte input, output;

    AndroidWinUsb(SafeFileHandle file, UsbHandle usb, byte input, byte output)
        => (this.file, this.usb, this.input, this.output) = (file, usb, input, output);

    internal static AndroidWinUsb? Find()
    {
        Guid guid = AdbInterface;
        const uint present = 0;
        if (CM_Get_Device_Interface_List_SizeW(out uint size, ref guid, 0, present) != 0 || size is < 2 or > 65536)
            return null;
        var names = new char[size];
        if (CM_Get_Device_Interface_ListW(ref guid, 0, names, size, present) != 0) return null;
        string[] paths = new string(names).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (paths.Length > 1)
            throw new InvalidOperationException("Disconnect other Android USB devices and try again.");
        return paths.Length == 1 ? TryOpen(paths[0]) : null;
    }

    static AndroidWinUsb? TryOpen(string path)
    {
        var file = CreateFileW(path, 0xc0000000, 0, 0, 3, 0x40000000, 0);
        UsbHandle? usb = null;
        bool ready = false;
        try
        {
            if (file.IsInvalid || !WinUsb_Initialize(file, out usb)) return null;
            byte* descriptor = stackalloc byte[9];
            if (!WinUsb_QueryInterfaceSettings(usb, 0, descriptor) ||
                descriptor[4] != 2 || descriptor[5] != 0xff || descriptor[6] != 0x42 || descriptor[7] != 1)
                return null;

            byte input = 0, output = 0;
            for (byte index = 0; index < 2; index++)
            {
                if (!WinUsb_QueryPipe(usb, 0, index, out var pipe) || pipe.Type != 2) return null;
                if ((pipe.Id & 0x80) != 0) input = pipe.Id;
                else output = pipe.Id;
            }

            uint readTimeout = 250, writeTimeout = 1000;
            byte terminate = 1;
            if (input == 0 || output == 0 ||
                !WinUsb_SetPipePolicy(usb, input, 3, sizeof(uint), &readTimeout) ||
                !WinUsb_SetPipePolicy(usb, output, 3, sizeof(uint), &writeTimeout) ||
                !WinUsb_SetPipePolicy(usb, output, 1, sizeof(byte), &terminate)) return null;

            ready = true;
            return new AndroidWinUsb(file, usb, input, output);
        }
        finally
        {
            if (!ready)
            {
                usb?.Dispose();
                file.Dispose();
            }
        }
    }

    public void ReadExactly(Span<byte> destination, CancellationToken token)
    {
        while (!destination.IsEmpty)
        {
            token.ThrowIfCancellationRequested();
            fixed (byte* data = destination)
            {
                if (WinUsb_ReadPipe(usb, input, data, destination.Length, out int size, 0))
                {
                    destination = destination[size..];
                    continue; // A zero-length USB packet can terminate the preceding transfer.
                }
                int error = Marshal.GetLastWin32Error();
                if (error != 121) throw new IOException("The Android USB read failed.", new Win32Exception(error));
            }
        }
    }

    public void Write(ReadOnlySpan<byte> data, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        fixed (byte* pointer = data)
        {
            if (!WinUsb_WritePipe(usb, output, pointer, data.Length, out int size, 0) || size != data.Length)
                throw new IOException("The Android USB write failed.");
        }
    }

    public void Abort()
    {
        WinUsb_AbortPipe(usb, input);
        WinUsb_AbortPipe(usb, output);
    }

    public void Dispose()
    {
        usb.Dispose();
        file.Dispose();
    }

    sealed class UsbHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => WinUsb_Free(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PipeInformation
    {
        public int Type;
        public byte Id;
        public ushort MaxPacketSize;
        public byte Interval;
    }

    [DllImport("cfgmgr32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern int CM_Get_Device_Interface_List_SizeW(out uint size, ref Guid guid, nint device, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern int CM_Get_Device_Interface_ListW(ref Guid guid, nint device, [Out] char[] names, uint size, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint mode, uint flags, nint template);
    [DllImport("winusb.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern bool WinUsb_Initialize(SafeFileHandle file, out UsbHandle usb);
    [DllImport("winusb.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern bool WinUsb_Free(nint usb);
    [DllImport("winusb.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern bool WinUsb_QueryInterfaceSettings(UsbHandle usb, byte alternate, byte* descriptor);
    [DllImport("winusb.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern bool WinUsb_QueryPipe(UsbHandle usb, byte alternate, byte index, out PipeInformation pipe);
    [DllImport("winusb.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern bool WinUsb_SetPipePolicy(UsbHandle usb, byte pipe, uint policy, uint size, void* value);
    [DllImport("winusb.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern bool WinUsb_ReadPipe(UsbHandle usb, byte pipe, byte* buffer, int length, out int transferred, nint overlapped);
    [DllImport("winusb.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern bool WinUsb_WritePipe(UsbHandle usb, byte pipe, byte* buffer, int length, out int transferred, nint overlapped);
    [DllImport("winusb.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern bool WinUsb_AbortPipe(UsbHandle usb, byte pipe);
}
