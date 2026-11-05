using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace XenoCapture.Engine;

internal sealed class AndroidUsbCaptureBackend(bool detectUsb = true, bool playAudio = false) : ICaptureBackend
{
    const string HelperFolder = "android";
    static readonly (string Name, string Sha256)[] HelperFiles =
    [
        ("adb.exe", "B4A6B455702684652CCCF7B46258B29E653538904359A58FD4931CF3EF286B3F"),
        ("AdbWinApi.dll", "C1D653030B4BDE65D3E07E4D0B0979E17BE56DF1436CDD15528630F27808050D"),
        ("AdbWinUsbApi.dll", "0710E894D9B40F71A670C13C694079D564C92C1279DA382CFE4850983AAEBE1B"),
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
    Process? adb, scrcpy;
    readonly object playbackGate = new();
    bool playbackClosing;
    WindowCaptureBackend? capture;

    int port;
    string folder = "";
    internal int ServerPort => port;
    internal int ServerProcessId { get; private set; }

    public PixelLayout Layout => PixelLayout.Bgr32;
    public int AudioRate => 0;
    public int AudioChannels => 0;
    public bool RequiresExternalShutdown => false;

    internal static bool IsPackaged => File.Exists(Path.Combine(AppContext.BaseDirectory, HelperFolder, "scrcpy.exe"));

    public void Open(CancellationToken token)
    {
        cancellation = token;
        folder = Path.Combine(AppContext.BaseDirectory, HelperFolder);
        VerifyPackage(folder, token);
        job = new AndroidProcessJob();
        // An ephemeral port avoids taking over an existing adb server.
        adb = job.Start(HelperProcess("adb.exe", "-L", "tcp:0", "server", "nodaemon"));
        ServerProcessId = adb.Id;
        long start = Stopwatch.GetTimestamp();
        do
        {
            token.ThrowIfCancellationRequested();
            if (adb.HasExited) throw new InvalidOperationException("The Android USB service could not start.");
            port = AndroidProcessJob.FindLoopbackPort(adb.Id);
            if (port != 0 && IsAdbReady(port, token)) return;
            if (token.WaitHandle.WaitOne(40)) token.ThrowIfCancellationRequested();
        } while (Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(4));
        throw new TimeoutException("The Android USB service did not open its local port.");
    }

    static bool IsAdbReady(int port, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(500));
        using var probe = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            probe.ConnectAsync(IPAddress.Loopback, port, deadline.Token).AsTask().GetAwaiter().GetResult();
            using var stream = probe.GetStream();
            stream.WriteAsync("000chost:version"u8.ToArray(), deadline.Token).AsTask().GetAwaiter().GetResult();
            var status = new byte[4];
            stream.ReadExactlyAsync(status, deadline.Token).AsTask().GetAwaiter().GetResult();
            return status.AsSpan().SequenceEqual("OKAY"u8);
        }
        catch (SocketException) { return false; }
        catch (IOException) { return false; }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return false; }
    }

    public Packet? Read()
    {
        if (capture == null) StartPreview();
        return capture!.Read();
    }

    void StartPreview()
    {
        using (var waiting = job!.Start(HelperProcess("adb.exe", "-d", "wait-for-device")))
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(75));
            try { waiting.WaitForExitAsync(deadline.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                throw new TimeoutException("Connect one Android phone over USB, unlock it, enable USB debugging and approve the phone prompt. Close other ADB tools if they have the USB device open.");
            }
            if (waiting.ExitCode != 0)
                throw new InvalidOperationException("ADB could not select one USB phone. Disconnect other phones and try again.");
        }

        cancellation.ThrowIfCancellationRequested();
        if (adb!.HasExited) throw new InvalidOperationException("The Android USB service stopped.");
        string title = "Xeno Android preview " + Guid.NewGuid().ToString("N")[..8];
        // Windows Graphics Capture needs a rendered window. Keep the helper beyond the desktop.
        var preview = HelperProcess("scrcpy.exe", "--select-usb", "--no-control",
            "--no-clipboard-autosync", "--window-borderless", "--window-x=30000", "--window-y=30000",
            "--max-size=1920", "--max-fps=60", "--video-buffer=0", "--window-title=" + title);
        if (playAudio)
        {
            preview.ArgumentList.Add("--audio-source=output");
            preview.ArgumentList.Add("--audio-codec=raw");
        }
        else preview.ArgumentList.Add("--no-audio");
        preview.Environment["SDL_WINDOW_ACTIVATE_WHEN_SHOWN"] = "0";
        lock (playbackGate) scrcpy = job!.Start(preview);
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

    ProcessStartInfo HelperProcess(string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(folder, executable))
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        foreach (string name in new[]
        {
            "ADB_SERVER_SOCKET", "ANDROID_ADB_SERVER_ADDRESS", "ANDROID_ADB_SERVER_PORT", "ADB_SERVER_PORT",
            "ADB_VENDOR_KEYS", "ADB_TRACE", "ANDROID_SERIAL", "SCRCPY_SERVER_PATH", "SCRCPY_ICON_PATH"
        }) info.Environment.Remove(name);

        // Limit selection to cable-connected devices, even when wireless debugging exists on the PC.
        info.Environment["ADB_USB"] = detectUsb ? "1" : "0";
        info.Environment["ADB_MDNS_AUTO_CONNECT"] = "0";
        info.Environment["ADB_LOCAL_TRANSPORT_MAX_PORT"] = "0";
        if (port != 0) info.Environment["ADB_SERVER_SOCKET"] = $"tcp:127.0.0.1:{port}";
        info.Environment["ADB"] = Path.Combine(folder, "adb.exe");
        info.Environment["SCRCPY_SERVER_PATH"] = Path.Combine(folder, "scrcpy-server");
        info.Environment["SCRCPY_ICON_PATH"] = Path.Combine(folder, "scrcpy.png");
        return info;
    }

    internal static void VerifyPackage(string path, CancellationToken token)
    {
        if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Install the verified Android USB helper beside XenoCapture.exe.");
        var entries = Directory.EnumerateFileSystemEntries(path).ToArray();
        if (entries.Length != HelperFiles.Length)
            throw new InvalidOperationException("The Android USB helper folder does not match the verified package.");

        foreach (var (name, hash) in HelperFiles)
        {
            token.ThrowIfCancellationRequested();
            string file = Path.Combine(path, name);
            if (!File.Exists(file) || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The Android USB helper folder is incomplete.");
            using var data = File.OpenRead(file);
            if (Convert.ToHexString(SHA256.HashData(data)) != hash)
                throw new InvalidOperationException("An Android USB helper file failed verification.");
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
                job?.Dispose();
                try
                {
                    if (adb?.WaitForExit(900) == false)
                        throw new InvalidOperationException("The Android USB service did not exit.");
                }
                finally
                {
                    scrcpy?.Dispose();
                    adb?.Dispose();
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

    [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

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

    internal Process Start(ProcessStartInfo info)
    {
        var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start an Android USB helper.");
        try
        {
            if (!AssignProcessToJobObject(handle, process.SafeHandle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not attach the Android USB helper to its session.");
            // Helper output may contain phone identifiers. Drain it without retaining it.
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            finally { process.Dispose(); }
            throw;
        }
    }

    internal static int FindLoopbackPort(int processId)
    {
        const int rowSize = 24;
        int size = 0;
        GetExtendedTcpTable(0, ref size, false, 2, 3, 0);
        if (size < 4 || size > 16 * 1024 * 1024) return 0;
        int allocated = size;
        nint table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, false, 2, 3, 0) != 0) return 0;
            int count = Marshal.ReadInt32(table);
            if (count < 0 || count > (allocated - 4) / rowSize) return 0;
            for (int index = 0; index < count; index++)
            {
                nint row = table + 4 + index * rowSize;
                if (Marshal.ReadInt32(row, 20) == processId && Marshal.ReadInt32(row, 4) == 0x0100007f)
                    return (ushort)System.Net.IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(row, 8));
            }
            return 0;
        }
        finally { Marshal.FreeHGlobal(table); }
    }

    public void Dispose() => handle.Dispose();
}
