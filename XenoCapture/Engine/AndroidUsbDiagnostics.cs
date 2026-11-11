using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Tasks;

namespace XenoCapture.Engine;

internal static class AndroidUsbDiagnostics
{
    internal static async Task Run(string directory)
    {
        var firstBackend = new AndroidUsbCaptureBackend(detectUsb: false);
        var secondBackend = new AndroidUsbCaptureBackend(detectUsb: false);
        using var smooth = new AndroidUsbCaptureBackend(detectUsb: false, profile: AndroidPreviewProfile.Smooth);
        using var highDetail = new AndroidUsbCaptureBackend(detectUsb: false, playAudio: true, profile: AndroidPreviewProfile.HighDetail);
        CheckPreviewOptions(firstBackend, 1280, 30, "4M", playAudio: false);
        CheckPreviewOptions(smooth, 1280, 60, "5M", playAudio: false);
        CheckPreviewOptions(highDetail, 1920, 60, "8M", playAudio: true);
        CheckLibraryOverride();
        await MainWindow.CheckPhonePreviewDefaults();

        var first = new CaptureStream(() => firstBackend, TimeSpan.FromSeconds(2));
        var second = new CaptureStream(() => secondBackend, TimeSpan.FromSeconds(2));
        try
        {
            await first.Start().WaitAsync(TimeSpan.FromSeconds(7));
            await second.Start().WaitAsync(TimeSpan.FromSeconds(7));
            int firstPort = firstBackend.PreviewBridge!.Port, secondPort = secondBackend.PreviewBridge!.Port;
            if (firstPort == 0 || secondPort == 0 || firstPort == secondPort)
                throw new InvalidOperationException("Android USB sessions did not get separate ports.");
            if (!await IsListening(firstPort) || !await IsListening(secondPort))
                throw new InvalidOperationException("A private media listener did not start.");

            string firstFolder = firstBackend.SessionHelperDirectory ?? throw new InvalidOperationException("Missing first helper directory.");
            string secondFolder = secondBackend.SessionHelperDirectory ?? throw new InvalidOperationException("Missing second helper directory.");
            string originalFolder = Path.Combine(AppContext.BaseDirectory, "android");
            if (firstFolder == secondFolder || firstFolder == originalFolder || secondFolder == originalFolder)
                throw new InvalidOperationException("Sessions did not stage their own verified helpers.");
            CheckPrivateAccess(firstFolder);
            CheckPrivateAccess(secondFolder);
            if (!IsWriteDenied(Path.Combine(firstFolder, "scrcpy-server")) || !IsWriteDenied(Path.Combine(secondFolder, "scrcpy-server")))
                throw new InvalidOperationException("A staged helper file is writable during capture.");
            await CheckCommandAccess(firstBackend, firstFolder);
            await AndroidUsbProtocolDiagnostics.Run(firstBackend);
            using (var previewJob = new AndroidProcessJob())
            {
                var info = firstBackend.CreatePreviewProcess("Diagnostic phone preview");
                info.ArgumentList.Clear();
                info.ArgumentList.Add("--version");
                using var process = previewJob.Start(info);
                if (!process.WaitForExit(4000) || process.ExitCode != 0)
                    throw new InvalidOperationException("The protected scrcpy helper did not start.");
            }
            await first.Stop();
            if (await IsListening(firstPort) || Directory.Exists(firstFolder))
                throw new InvalidOperationException("The first USB session was not cleaned up.");
            if (!await IsListening(secondPort) || !IsWriteDenied(Path.Combine(secondFolder, "scrcpy-server")))
                throw new InvalidOperationException("Disconnecting one session affected another session.");
            await second.Stop();
            if (Directory.Exists(secondFolder))
                throw new InvalidOperationException("The second helper directory stayed behind after disconnect.");
            if (await IsListening(secondPort))
                throw new InvalidOperationException("The second media listener stayed open after disconnect.");

            File.WriteAllText(Path.Combine(directory, "results.json"),
                JsonSerializer.Serialize(new { passed = true, previewProfiles = true, aiOffByDefault = true,
                    isolatedListeners = true, stoppedWhileWaiting = true, protectedHelperFiles = true,
                    ignoredLibraryOverride = true, privateHelperCopies = true, privateCommands = true, mediaPeerIdentity = true, usbProtocol = true, physicalPhone = false }));
        }
        finally
        {
            await Task.WhenAll(first.Stop(), second.Stop());
        }
    }

    static void CheckPreviewOptions(AndroidUsbCaptureBackend backend, int size, int fps, string bitrate, bool playAudio)
    {
        var args = backend.CreatePreviewProcess("Diagnostic phone preview").ArgumentList;
        if (!args.Contains($"--max-size={size}") || !args.Contains($"--max-fps={fps}")
            || !args.Contains("--video-bit-rate=" + bitrate) || !args.Contains("--video-buffer=0")
            || !args.Contains("--force-adb-forward") || !args.Contains("--select-usb") || !args.Contains("--no-control") || !args.Contains("--no-clipboard-autosync")
            || args.Contains("--audio-source=output") != playAudio || args.Contains("--no-audio") == playAudio)
            throw new InvalidOperationException("The Android preview did not apply the expected launch options.");
    }

    static void CheckLibraryOverride()
    {
        const string variable = "SDL3_DYNAMIC_API";
        string? previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "diagnostic-unused.dll");
            using var backend = new AndroidUsbCaptureBackend(detectUsb: false);
            if (backend.CreatePreviewProcess("Diagnostic phone preview").Environment.ContainsKey(variable))
                throw new InvalidOperationException("The Android helper inherited an SDL library override.");
        }
        finally { Environment.SetEnvironmentVariable(variable, previous); }
    }

    static void CheckPrivateAccess(string folder)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new DirectoryInfo(folder).GetAccessControl(AccessControlSections.Access);
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToArray();
        if (!security.AreAccessRulesProtected || rules.Length != 2 ||
            !rules.Any(rule => rule.IdentityReference.Equals(identity.User)) ||
            !rules.Any(rule => rule.IdentityReference.Equals(system)))
            throw new InvalidOperationException("The staged helpers are accessible outside the current Windows account and SYSTEM.");

        string addedFile = Path.Combine(folder, "diagnostic-new-file");
        try
        {
            using var probe = new FileStream(addedFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (UnauthorizedAccessException) { return; }
        File.Delete(addedFile);
        throw new InvalidOperationException("The live helper directory accepts new files.");
    }

    static async Task CheckCommandAccess(AndroidUsbCaptureBackend backend, string folder)
    {
        var bridge = backend.PreviewBridge!;
        ProcessStartInfo Command(string name, string command)
        {
            var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "XenoCapture.exe"))
            {
                WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true
            };
            info.Environment[AndroidPreviewBridge.PipeVariable] = name;
            info.Environment[AndroidPreviewBridge.ParentVariable] = Environment.ProcessId.ToString();
            info.ArgumentList.Add(command);
            return info;
        }

        using (var trusted = backend.PreviewJob!.Start(Command(bridge.PipeName, "start-server")))
        {
            await trusted.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6));
            if (trusted.ExitCode != 0) throw new InvalidOperationException("The private command pipe rejected its helper.");
        }
        using (var untrusted = Process.Start(Command(bridge.PipeName, "start-server"))!)
        {
            await untrusted.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6));
            if (untrusted.ExitCode == 0) throw new InvalidOperationException("The private command pipe accepted an outside process.");
        }
        using (var outsideJob = new AndroidProcessJob())
        using (var other = outsideJob.Start(Command(bridge.PipeName, "start-server")))
        {
            await other.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6));
            if (other.ExitCode == 0) throw new InvalidOperationException("The private command pipe accepted a different job.");
        }
        using (var unsupported = backend.PreviewJob.Start(Command(bridge.PipeName, "kill-server")))
        {
            await unsupported.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6));
            if (unsupported.ExitCode == 0) throw new InvalidOperationException("The private command pipe allowed an unsupported command.");
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var outsider = new TcpClient(AddressFamily.InterNetwork);
        await outsider.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var peer = await listener.AcceptTcpClientAsync();
        if (backend.PreviewJob.OwnsPeer(peer.Client))
            throw new InvalidOperationException("An outside process passed the media peer check.");
    }

    static bool IsWriteDenied(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            return false;
        }
        catch (UnauthorizedAccessException) { return true; }
        catch (IOException error) when ((error.HResult & 0xffff) == 32) { return true; }
    }

    static async Task<bool> IsListening(int port)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(2));
            return true;
        }
        catch (Exception error) when (error is SocketException or TimeoutException) { return false; }
    }
}
