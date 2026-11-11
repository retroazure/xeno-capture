using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace XenoCapture.Engine;

internal static unsafe class AndroidHelperLaunch
{
    const nuint HandleList = 0x20002, MitigationPolicy = 0x20007, JobList = 0x2000d;
    const ulong LocalTrustedImages = (1UL << 52) | (1UL << 56) | (1UL << 60);
    const uint CreateFlags = 0x08000000 | 0x00080000 | 0x00000400 | 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    struct SecurityAttributes
    {
        public uint Size;
        public nint Descriptor;
        public int Inheritable;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfo
    {
        public uint Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, Width, Height, Columns, Rows, Fill, Flags;
        public ushort ShowWindow, ReservedCount;
        public nint ReservedBytes, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfoEx
    {
        public StartupInfo Info;
        public nint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInfo
    {
        public nint Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, void* value,
        nuint size, nint previous, nint returnedSize);

    [DllImport("kernel32.dll")]
    static extern void DeleteProcThreadAttributeList(nint list);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share, ref SecurityAttributes security,
        uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcess(string executable, char* commandLine, nint processSecurity, nint threadSecurity,
        bool inheritHandles, uint flags, char* environment, string directory, ref StartupInfoEx startup,
        out ProcessInfo process);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint ResumeThread(SafeWaitHandle thread);

    [DllImport("kernel32.dll")]
    static extern bool TerminateProcess(SafeProcessHandle process, uint code);

    internal static Process Start(ProcessStartInfo info, SafeFileHandle job)
    {
        var security = new SecurityAttributes { Size = (uint)sizeof(SecurityAttributes), Inheritable = 1 };
        // ADB may log device identifiers. Send its output to NUL without creating reader tasks.
        using var quiet = CreateFile("NUL", 0xc0000000, 3, ref security, 3, 0, 0);
        if (quiet.IsInvalid) throw LastError();
        nint quietHandle = quiet.DangerousGetHandle();
        nint jobHandle = job.DangerousGetHandle();
        ulong policy = LocalTrustedImages;
        nuint size = 0;
        InitializeProcThreadAttributeList(0, 3, 0, ref size);
        if (size == 0) throw LastError();
        nint attributes = Marshal.AllocHGlobal(checked((nint)size));
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 3, 0, ref size)) throw LastError();
            try { return Start(info, attributes, &quietHandle, &jobHandle, &policy); }
            finally
            {
                DeleteProcThreadAttributeList(attributes);
                GC.KeepAlive(job);
            }
        }
        finally { Marshal.FreeHGlobal(attributes); }
    }

    static Process Start(ProcessStartInfo info, nint attributes, nint* quietHandle, nint* jobHandle, ulong* policy)
    {
        // Apply image search policy and job membership before the loader runs.
        if (!UpdateProcThreadAttribute(attributes, 0, HandleList, quietHandle, (nuint)sizeof(nint), 0, 0)
            || !UpdateProcThreadAttribute(attributes, 0, MitigationPolicy, policy, sizeof(ulong), 0, 0)
            || !UpdateProcThreadAttribute(attributes, 0, JobList, jobHandle, (nuint)sizeof(nint), 0, 0))
            throw LastError();

        var startup = new StartupInfoEx
        {
            Info = new StartupInfo
            {
                Size = (uint)sizeof(StartupInfoEx), Flags = 0x100,
                Input = *quietHandle, Output = *quietHandle, Error = *quietHandle
            },
            Attributes = attributes
        };
        var command = new StringBuilder();
        AppendArgument(command, info.FileName);
        foreach (string argument in info.ArgumentList)
        {
            command.Append(' ');
            AppendArgument(command, argument);
        }
        char[] commandLine = (command.ToString() + '\0').ToCharArray();
        string environment = string.Join('\0', info.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";

        ProcessInfo result;
        fixed (char* arguments = commandLine, variables = environment)
            if (!CreateProcess(info.FileName, arguments, 0, 0, true, CreateFlags, variables,
                info.WorkingDirectory, ref startup, out result))
                throw LastError();
        using var processHandle = new SafeProcessHandle(result.Process, ownsHandle: true);
        using var threadHandle = new SafeWaitHandle(result.Thread, ownsHandle: true);
        Process? process = null;
        try
        {
            process = Process.GetProcessById(checked((int)result.ProcessId));
            _ = process.SafeHandle;
            if (ResumeThread(threadHandle) == uint.MaxValue) throw LastError();
            return process;
        }
        catch
        {
            TerminateProcess(processHandle, 1);
            process?.Dispose();
            throw;
        }
    }

    // Quote for the Windows argument parser, including trailing backslashes and embedded quotes.
    static void AppendArgument(StringBuilder command, string argument)
    {
        command.Append('"');
        int slashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }
            command.Append('\\', character == '"' ? slashes * 2 + 1 : slashes).Append(character);
            slashes = 0;
        }
        command.Append('\\', slashes * 2).Append('"');
    }

    static Win32Exception LastError() => new(Marshal.GetLastWin32Error(), "Could not launch a protected Android USB helper.");
}
