using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace XenoCapture.Engine;

internal enum AndroidPreviewProfile { Responsive, Smooth, HighDetail }

internal sealed class AndroidUsbCaptureBackend(bool detectUsb = true, bool playAudio = false,
    AndroidPreviewProfile profile = AndroidPreviewProfile.Responsive) : ICaptureBackend
{
    const string HelperFolder = "android";
    static readonly (string Name, string Sha256)[] HelperFiles =
    [
        ("avcodec-63.dll", "159928E3BBE7BEACA046CCF831FDF44F1E5559C0E63417F007889A6BB2C5DE88"),
        ("avformat-63.dll", "4DBB0C1C8262F7AE45E7EDC4866E26F53B358BF4FC7A0B971F15D1F2D7BAF22F"),
        ("avutil-61.dll", "52EB504CED0EA6D9E936D348DF51C8AAEEC1CAE8FA4A7DBA339FEA87D7CCE725"),
        ("disconnected.png", "E394873CD3E2CC3AB0CCA6212B10ED2A8A0FAD11A05675C8A9FA6F26F3AE12C0"),
        ("libusb-1.0.dll", "0C82E969C046D816507A2FE1E57253A334CB203BD6EA223194AD17C007154F32"),
        ("LICENSE.txt", "01C12035BF35AF37241298DC7AD538EB2A07E5C940437BC6876FEEAA9D1951D0"),
        ("scrcpy-server", "764EB6F79811D5211FE9DF341120882BA9994C7A61B897D7BF3FB662E53BC536"),
        ("scrcpy.exe", "64BCEE493D9243DAB1E84093928014348738E31F3225E67558210E6B6CFB9CD6"),
        ("scrcpy.png", "8E8CA237898FAA16014CDD118396AF53405B423F3DB0508C50CC3EDCE08EB313"),
        ("SDL3.dll", "288AAF5B4F246BD84C276351867E3132CFCD9B0A9D25FB679C8D94B30CDF333E"),
        ("swresample-7.dll", "7B57E891BF09C43422D79F98CF9D1830C4B1D48A251423CE5FB8C2048EB8FC1E")
    ];

    CancellationToken cancellation;
    AndroidProcessJob? job;
    VerifiedPackage? package;
    FileStream? previewClient;
    AndroidPreviewBridge? bridge;
    Process? scrcpy;
    readonly object playbackGate = new();
    bool playbackClosing;
    WindowCaptureBackend? capture;

    string folder = "";
    internal AndroidPreviewBridge? PreviewBridge => bridge;
    internal AndroidProcessJob? PreviewJob => job;
    internal string? SessionHelperDirectory => package?.Folder;

    public PixelLayout Layout => PixelLayout.Bgr32;
    public int AudioRate => 0;
    public int AudioChannels => 0;
    public bool RequiresExternalShutdown => false;

    internal static bool IsPackaged => File.Exists(Path.Combine(AppContext.BaseDirectory, HelperFolder, "scrcpy.exe"));

    public void Open(CancellationToken token)
    {
        cancellation = token;
        package = new VerifiedPackage(Path.Combine(AppContext.BaseDirectory, HelperFolder), token);
        folder = package.Folder;
        // scrcpy starts this executable as a command shim. Keep its file pinned
        // for the lifetime of the job, just like the other helper files.
        string executable = Path.Combine(AppContext.BaseDirectory, "XenoCapture.exe");
        if ((File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The Android USB preview needs a local XenoCapture.exe.");
        previewClient = File.OpenRead(executable);
        job = new AndroidProcessJob();
        bridge = new AndroidPreviewBridge(job, folder);
    }

    public Packet? Read()
    {
        if (!detectUsb)
        {
            cancellation.WaitHandle.WaitOne();
            return null;
        }
        if (capture == null) StartPreview();
        return capture!.Read();
    }

    void StartPreview()
    {
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(75));
            try { bridge!.Attach(AndroidAdbConnection.Connect(deadline.Token)); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                throw new TimeoutException("Connect one Android phone with a USB data cable, unlock it, and approve USB debugging. Check the Windows ADB (WinUSB) driver and close other ADB tools if they have the device open.");
            }
        }

        cancellation.ThrowIfCancellationRequested();
        string title = "Xeno Android preview " + Guid.NewGuid().ToString("N")[..8];
        lock (playbackGate) scrcpy = job!.Start(CreatePreviewProcess(title));
        long start = Stopwatch.GetTimestamp();
        do
        {
            cancellation.ThrowIfCancellationRequested();
            if (scrcpy.HasExited) throw new InvalidOperationException("The Android preview closed. Verify the phone is unlocked and USB debugging is approved.");
            scrcpy.Refresh();
            nint window = scrcpy.MainWindowHandle;
            if (window != 0 && scrcpy.MainWindowTitle == title)
            {
                capture = new WindowCaptureBackend(WindowCaptureBackend.CreateItem(window));
                capture.Open(cancellation);
                return;
            }
            if (cancellation.WaitHandle.WaitOne(40)) cancellation.ThrowIfCancellationRequested();
        } while (Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(12));
        throw new TimeoutException("The phone connected but did not open a preview. Unlock it and try again.");
    }

    internal ProcessStartInfo CreatePreviewProcess(string title)
    {
        var (size, fps, bitrate) = profile switch
        {
            AndroidPreviewProfile.Smooth => ("1280", "60", "5M"),
            AndroidPreviewProfile.HighDetail => ("1920", "60", "8M"),
            _ => ("1280", "30", "4M")
        };
        // Windows Graphics Capture needs a rendered window. Keep the helper beyond the desktop.
        var preview = HelperProcess("scrcpy.exe", "--select-usb", "--no-control",
            "--no-clipboard-autosync", "--force-adb-forward", "--window-borderless", "--window-x=30000", "--window-y=30000",
            "--max-size=" + size, "--max-fps=" + fps, "--video-bit-rate=" + bitrate,
            "--video-buffer=0", "--window-title=" + title);
        if (bridge != null)
        {
            preview.ArgumentList.Add("--port=" + bridge.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            preview.Environment[AndroidPreviewBridge.PipeVariable] = bridge.PipeName;
            preview.Environment[AndroidPreviewBridge.ParentVariable] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if (playAudio)
        {
            preview.ArgumentList.Add("--audio-source=output");
            preview.ArgumentList.Add("--audio-codec=raw");
        }
        else preview.ArgumentList.Add("--no-audio");
        preview.Environment["SDL_WINDOW_ACTIVATE_WHEN_SHOWN"] = "0";
        return preview;
    }

    ProcessStartInfo HelperProcess(string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(folder, executable))
        {
            WorkingDirectory = folder,
            UseShellExecute = false
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        foreach (string name in new[]
        {
            "ADB_SERVER_SOCKET", "ANDROID_ADB_SERVER_ADDRESS", "ANDROID_ADB_SERVER_PORT", "ADB_SERVER_PORT",
            "ADB_VENDOR_KEYS", "ADB_TRACE", "ANDROID_SERIAL", "SCRCPY_SERVER_PATH", "SCRCPY_ICON_PATH", AndroidPreviewBridge.PipeVariable, AndroidPreviewBridge.ParentVariable,
            "SDL3_DYNAMIC_API", "SDL_OPENGL_LIBRARY", "SDL_EGL_LIBRARY", "SDL_VULKAN_LIBRARY"
        }) info.Environment.Remove(name);

        foreach (string name in info.Environment.Keys.ToArray())
        {
            if (name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("COMPLUS_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("COR_", StringComparison.OrdinalIgnoreCase)) info.Environment.Remove(name);
        }

        info.Environment["PATH"] = Environment.SystemDirectory;
        info.Environment["ADB"] = Path.Combine(AppContext.BaseDirectory, "XenoCapture.exe");
        info.Environment["SCRCPY_SERVER_PATH"] = Path.Combine(folder, "scrcpy-server");
        info.Environment["SCRCPY_ICON_PATH"] = Path.Combine(folder, "scrcpy.png");
        return info;
    }

    sealed class VerifiedPackage : IDisposable
    {
        readonly List<FileStream> files = new(HelperFiles.Length);
        internal string Folder { get; }

        internal VerifiedPackage(string path, CancellationToken token)
        {
            if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Install the verified Android USB helper beside XenoCapture.exe.");
            // Permit old installations in place; these legacy ADB files are never copied or run.
            string[] legacyFiles = ["adb.exe", "AdbWinApi.dll", "AdbWinUsbApi.dll"];
            if (Directory.EnumerateFileSystemEntries(path).Select(Path.GetFileName).Any(name =>
                !legacyFiles.Contains(name, StringComparer.OrdinalIgnoreCase) &&
                !HelperFiles.Any(file => string.Equals(file.Name, name, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException("The Android USB helper folder does not match the verified package.");

            Folder = CreatePrivateDirectory();
            try
            {
                foreach (var (name, hash) in HelperFiles)
                {
                    token.ThrowIfCancellationRequested();
                    string source = Path.Combine(path, name);
                    if (!File.Exists(source) || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidOperationException("The Android USB helper folder is incomplete.");

                    string destination = Path.Combine(Folder, name);
                    using (var input = File.OpenRead(source))
                    {
                        if (input.Length is < 1 or > 16 * 1024 * 1024)
                            throw new InvalidOperationException("An Android USB helper file has an unexpected size.");
                        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        input.CopyTo(output);
                    }
                    // Verify the exact copy the loader will use and deny changes while it runs.
                    var data = File.OpenRead(destination);
                    files.Add(data);
                    if (Convert.ToHexString(SHA256.HashData(data)) != hash)
                        throw new InvalidOperationException("An Android USB helper file failed verification.");
                }
                new DirectoryInfo(Folder).SetAccessControl(DirectoryAccess(writable: false));
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        static string CreatePrivateDirectory()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!Directory.Exists(root))
                throw new InvalidOperationException("The Android USB helper needs a local user data directory.");
            string folder = Path.Combine(root, "XenoCapture.Android." + Guid.NewGuid().ToString("N"));
            DirectoryAccess(writable: true, setOwner: true).CreateDirectory(folder);
            return folder;
        }

        static DirectorySecurity DirectoryAccess(bool writable, bool setOwner = false)
        {
            using var identity = WindowsIdentity.GetCurrent();
            var owner = identity.User ?? throw new InvalidOperationException("Could not identify the current Windows user.");
            var security = new DirectorySecurity();
            if (setOwner) security.SetOwner(owner);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(owner,
                writable ? FileSystemRights.FullControl : FileSystemRights.ReadAndExecute,
                inheritance, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
            return security;
        }

        public void Dispose()
        {
            foreach (var file in files) file.Dispose();
            files.Clear();
            if (!Directory.Exists(Folder) || (File.GetAttributes(Folder) & FileAttributes.ReparsePoint) != 0) return;
            try { new DirectoryInfo(Folder).SetAccessControl(DirectoryAccess(writable: true)); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            foreach (var (name, _) in HelperFiles)
            {
                try { File.Delete(Path.Combine(Folder, name)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            try { Directory.Delete(Folder); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal float? AccessPlaybackVolume(float? requestedLevel)
    {
        lock (playbackGate)
        {
            return !playbackClosing && playAudio && scrcpy is { HasExited: false } process
                ? AudioSessionVolume.Access(process, requestedLevel) : null;
        }
    }

    public void Shutdown() { }

    public void Dispose()
    {
        lock (playbackGate) playbackClosing = true;
        try { capture?.Dispose(); }
        finally
        {
            try
            {
                if (scrcpy is { HasExited: false } && scrcpy.CloseMainWindow())
                    scrcpy.WaitForExit(350);
            }
            finally
            {
                try
                {
                    job?.Dispose();
                    scrcpy?.WaitForExit(900);

                }
                finally
                {
                    scrcpy?.Dispose();
                    try { bridge?.Dispose(); }
                    finally
                    {
                        previewClient?.Dispose();
                        package?.Dispose();
                    }
                }
            }
        }
    }
}

internal sealed class AndroidProcessJob : IDisposable
{
    readonly SafeFileHandle handle;

    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimits
    {
        public long PerProcessTime, PerJobTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint Priority, Scheduling;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Limits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern SafeFileHandle CreateJobObject(nint attributes, nint name);

    [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref Limits limits, int length);


    [DllImport("iphlpapi.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern uint GetExtendedTcpTable(nint table, ref int size, bool order, int family, int tableClass, uint reserved);

    internal AndroidProcessJob()
    {
        handle = CreateJobObject(0, 0);
        var limits = new Limits { Basic = new BasicLimits { Flags = 0x2000 } }; // Kill child processes on close.
        if (handle.IsInvalid || !SetInformationJobObject(handle, 9, ref limits, Marshal.SizeOf<Limits>()))
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "Could not isolate the Android USB helper processes.");
        }
    }

    internal Process Start(ProcessStartInfo info) => AndroidHelperLaunch.Start(info, handle);

    [DllImport("kernel32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern bool IsProcessInJob(SafeProcessHandle process, SafeFileHandle job, out bool result);

    internal bool Contains(uint pid)
    {
        using var process = OpenProcess(0x1000, false, pid);
        return !process.IsInvalid && !handle.IsClosed &&
            IsProcessInJob(process, handle, out bool result) && result;
    }

    internal bool OwnsPeer(Socket socket)
    {
        if (socket.LocalEndPoint is not IPEndPoint local || socket.RemoteEndPoint is not IPEndPoint peer ||
            !local.Address.Equals(IPAddress.Loopback) || !peer.Address.Equals(IPAddress.Loopback)) return false;

        const int ipv4 = 2, ownerConnections = 4, rowSize = 24, established = 5, loopback = 0x0100007f;
        int peerPort = (ushort)IPAddress.HostToNetworkOrder((short)peer.Port);
        int localPort = (ushort)IPAddress.HostToNetworkOrder((short)local.Port);
        int size = 0;
        GetExtendedTcpTable(0, ref size, false, ipv4, ownerConnections, 0);
        if (size < 4 || size > 16 * 1024 * 1024) return false;
        int allocated = size;
        nint table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, false, ipv4, ownerConnections, 0) != 0) return false;
            int count = Marshal.ReadInt32(table);
            if (count < 0 || count > (allocated - 4) / rowSize) return false;
            for (int index = 0; index < count; index++)
            {
                nint row = table + 4 + index * rowSize;
                // Look up the client side of this exact connection in the kernel.
                if (Marshal.ReadInt32(row) == established &&
                    Marshal.ReadInt32(row, 4) == loopback && Marshal.ReadInt32(row, 12) == loopback &&
                    (Marshal.ReadInt32(row, 8) & 0xffff) == peerPort &&
                    (Marshal.ReadInt32(row, 16) & 0xffff) == localPort)
                    return Contains((uint)Marshal.ReadInt32(row, 20));
            }
            return false;
        }
        finally { Marshal.FreeHGlobal(table); }
    }

    public void Dispose() => handle.Dispose();
}
