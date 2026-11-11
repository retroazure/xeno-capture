using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XenoCapture.Engine;

// The pinned scrcpy client gets a private, narrowly scoped adb substitute. The only
// TCP socket carries media, and only a process in this session's job can connect.
internal sealed class AndroidPreviewBridge : IDisposable
{
    internal const string PipeVariable = "XENO_ANDROID_PREVIEW_PIPE";
    internal const string ParentVariable = "XENO_ANDROID_PREVIEW_PARENT";
    const string Prefix = "XenoCapture.Android.";
    const string Serial = "XenoUsb";
    const string RemoteJar = "/data/local/tmp/scrcpy-server.jar";
    const string ServerClass = "com.genymobile.scrcpy.Server";
    const string ScrcpyVersion = "5.0.1";
    const int MaxRequest = 4096;
    readonly AndroidProcessJob job;
    readonly string serverPath;
    readonly CancellationTokenSource stopped = new();
    readonly TcpListener media = new(IPAddress.Loopback, 0);
    readonly SemaphoreSlim pipeSlots = new(8), mediaSlots = new(4);
    readonly PipeSecurity commandAccess = CreateCommandAccess();
    readonly Task pipeLoop;
    AndroidAdbConnection? device;
    string? target;
    int serverStarted;

    internal AndroidPreviewBridge(AndroidProcessJob job, string folder)
    {
        this.job = job;
        serverPath = Path.Combine(folder, "scrcpy-server");
        media.ExclusiveAddressUse = true;
        media.Start(8);
        Port = ((IPEndPoint)media.LocalEndpoint).Port;
        pipeLoop = AcceptCommandsAsync();
    }

    internal int Port { get; }
    internal string PipeName { get; } = Prefix + Guid.NewGuid().ToString("N");
    internal void Attach(AndroidAdbConnection connection) => Volatile.Write(ref device, connection);

    // The stock scrcpy process starts Xeno with adb-style arguments, not a GUI.
    internal static async Task<int> RunClientAsync(string pipeName, string[] args)
    {
        if (!pipeName.StartsWith(Prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(pipeName.AsSpan(Prefix.Length), "N", out _)) return 1;
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(3000).ConfigureAwait(false);
            if (!uint.TryParse(Environment.GetEnvironmentVariable(ParentVariable), NumberStyles.None,
                    CultureInfo.InvariantCulture, out uint parent) ||
                !GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint server) || parent != server) return 1;
            var request = JsonSerializer.SerializeToUtf8Bytes(args);
            if (request.Length > MaxRequest) return 1;
            byte[] size = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(size, (ushort)request.Length);
            await pipe.WriteAsync(size).ConfigureAwait(false);
            await pipe.WriteAsync(request).ConfigureAwait(false);
            byte[] status = new byte[1];
            await pipe.ReadExactlyAsync(status).ConfigureAwait(false);
            if (status[0] != 0) return 1;
            using var output = new MemoryStream();
            await pipe.CopyToAsync(output).ConfigureAwait(false);
            if (output.Length != 0)
            {
                using var stdout = Console.OpenStandardOutput();
                await stdout.WriteAsync(output.GetBuffer().AsMemory(0, (int)output.Length)).ConfigureAwait(false);
            }
            return 0;
        }
        catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return 1;
        }
    }

    static PipeSecurity CreateCommandAccess()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new InvalidOperationException("Could not identify the Android preview user.");
        var security = new PipeSecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.FullControl,
            System.Security.AccessControl.AccessControlType.Allow));
        return security;
    }

    async Task AcceptCommandsAsync()
    {
        try
        {
            while (!stopped.IsCancellationRequested)
            {
                await pipeSlots.WaitAsync(stopped.Token).ConfigureAwait(false);
                var pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 8,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, MaxRequest, MaxRequest, commandAccess);
                try { await pipe.WaitForConnectionAsync(stopped.Token).ConfigureAwait(false); }
                catch
                {
                    pipe.Dispose();
                    pipeSlots.Release();
                    throw;
                }
                _ = HandleCommandAsync(pipe);
            }
        }
        catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
    }

    async Task HandleCommandAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            try
            {
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) || !job.Contains(pid)) return;

                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                var size = new byte[2];
                await pipe.ReadExactlyAsync(size, deadline.Token).ConfigureAwait(false);
                int length = BinaryPrimitives.ReadUInt16LittleEndian(size);
                if (length is <= 0 or > MaxRequest) return;
                byte[] data = new byte[length];
                await pipe.ReadExactlyAsync(data, deadline.Token).ConfigureAwait(false);
                var args = JsonSerializer.Deserialize<string[]>(data);
                if (args == null || args.Any(static arg => arg == null)) return;
                deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                string? output = await ExecuteAsync(args, stopped.Token).ConfigureAwait(false);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                if (output == null) return;
                await pipe.WriteAsync(Encoding.ASCII.GetBytes("\0" + output), deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or JsonException or OperationCanceledException or SocketException or ObjectDisposedException) { }
            finally { pipeSlots.Release(); }
        }
    }

    async Task<string?> ExecuteAsync(string[] args, CancellationToken token)
    {
        if (args is ["start-server"]) return "";
        AndroidAdbConnection? usb = Volatile.Read(ref device);
        if (args is ["devices", "-l"])
            return usb == null ? "List of devices attached\r\n\r\n" :
                "List of devices attached\r\n" + Serial + " device usb:1-1 model:Android_USB transport_id:1\r\n\r\n";
        if (usb == null || args.Length < 3 || args[0] != "-s" || args[1] != Serial) return null;

        string port = "tcp:" + Port.ToString(CultureInfo.InvariantCulture);
        if (args is [_, _, "forward", var local, var endpoint] && local == port && IsScrcpyEndpoint(endpoint))
        {
            if (Interlocked.CompareExchange(ref target, endpoint, null) != null) return null;
            _ = AcceptMediaAsync(usb, endpoint);
            return "";
        }
        if (args is [_, _, "forward", "--remove", var remove] && remove == port)
        {
            media.Stop();
            return "";
        }
        if (args is [_, _, "push", var file, RemoteJar] && string.Equals(file, serverPath, StringComparison.OrdinalIgnoreCase))
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            await PushServerAsync(usb, deadline.Token).ConfigureAwait(false);
            return "";
        }
        if (args.Length >= 10 && args[2] == "shell" && args[3] == "CLASSPATH=" + RemoteJar &&
            args[4] == "app_process" && args[5] == "/" && args[6] == ServerClass && args[7] == ScrcpyVersion &&
            target != null && args[8] == "scid=" + target["localabstract:scrcpy_".Length..] &&
            args[9..].All(IsSafeOption) && args.Contains("control=false") && args.Contains("clipboard_autosync=false") &&
            args.Contains("tunnel_forward=true") && Interlocked.Exchange(ref serverStarted, 1) == 0)
        {
            string shell = string.Join(' ', args, 3, args.Length - 3);
            using var channel = await usb.OpenAsync("shell:" + shell, token).ConfigureAwait(false);
            // Keep the shim alive for scrcpy's server monitor. Do not retain device logs.
            while (await channel.ReadAsync(token).ConfigureAwait(false) != null) channel.Ack(token);
            return "";
        }
        return null;
    }

    static bool IsSafeOption(string option)
    {
        int separator = option.IndexOf('=');
        if (separator <= 0 || option.Length > 160) return false;
        string value = option[(separator + 1)..];
        return option[..separator] switch
        {
            "control" or "clipboard_autosync" or "audio" => value == "false",
            "tunnel_forward" => value == "true",
            "audio_source" => value == "output",
            "audio_codec" => value == "raw",
            "log_level" => value is "verbose" or "debug" or "info" or "warn" or "error",
            "video_bit_rate" or "max_size" or "max_fps" => value.Length > 0 &&
                value.All(static c => char.IsAsciiDigit(c) || c == '.'),
            _ => false
        };
    }

    static bool IsScrcpyEndpoint(string endpoint)
    {
        const string prefix = "localabstract:scrcpy_";
        return endpoint.Length == prefix.Length + 8 && endpoint.StartsWith(prefix, StringComparison.Ordinal) &&
            endpoint.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;
    }

    async Task PushServerAsync(AndroidAdbConnection usb, CancellationToken token)
    {
        using var channel = await usb.OpenAsync("sync:", token).ConfigureAwait(false);
        await channel.WriteAsync(SyncRequest("SEND"u8, Encoding.ASCII.GetBytes(RemoteJar + ",33216")), token).ConfigureAwait(false);
        using var file = File.OpenRead(serverPath);
        var buffer = new byte[8 + 64 * 1024];
        "DATA"u8.CopyTo(buffer);
        int length;
        while ((length = await file.ReadAsync(buffer.AsMemory(8), token).ConfigureAwait(false)) != 0)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), length);
            await channel.WriteAsync(buffer.AsMemory(0, length + 8), token).ConfigureAwait(false);
        }
        "DONE"u8.CopyTo(buffer);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await channel.WriteAsync(buffer.AsMemory(0, 8), token).ConfigureAwait(false);
        int received = 0;
        while (received < 8 && await channel.ReadAsync(token).ConfigureAwait(false) is { } status)
        {
            int size = Math.Min(status.Length, 8 - received);
            status.AsSpan(0, size).CopyTo(buffer.AsSpan(received));
            received += size;
            channel.Ack(token);
        }
        if (received != 8 || !buffer.AsSpan(0, 8).SequenceEqual("OKAY\0\0\0\0"u8))
            throw new IOException("The phone refused the verified Android preview helper.");
    }

    static byte[] SyncRequest(ReadOnlySpan<byte> verb, ReadOnlySpan<byte> data)
    {
        var request = new byte[8 + data.Length];
        verb.CopyTo(request);
        BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(4), data.Length);
        data.CopyTo(request.AsSpan(8));
        return request;
    }

    async Task AcceptMediaAsync(AndroidAdbConnection usb, string endpoint)
    {
        try
        {
            while (!stopped.IsCancellationRequested)
            {
                TcpClient client = await media.AcceptTcpClientAsync(stopped.Token).ConfigureAwait(false);
                if (!job.OwnsPeer(client.Client) || !mediaSlots.Wait(0))
                {
                    client.Dispose();
                    continue;
                }
                _ = RelayMediaAsync(client, usb, endpoint, stopped.Token);
            }
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException or InvalidOperationException or OperationCanceledException) { }
    }

    async Task RelayMediaAsync(TcpClient client, AndroidAdbConnection usb, string endpoint, CancellationToken token)
    {
        using (client)
        using (var done = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            try
            {
                client.NoDelay = true;
                NetworkStream output = client.GetStream();
                // No scrcpy control channel. Stop immediately if the peer sends data or disconnects.
                var disconnected = WatchPeerAsync(output, done);
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(done.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(3));
                    using var channel = await usb.OpenAsync(endpoint, deadline.Token).ConfigureAwait(false);
                    while (await channel.ReadAsync(done.Token).ConfigureAwait(false) is { } data)
                    {
                        await output.WriteAsync(data, done.Token).ConfigureAwait(false);
                        channel.Ack(done.Token);
                    }
                }
                finally
                {
                    done.Cancel();
                    await disconnected.ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException) { }
            finally
            {
                done.Cancel();
                mediaSlots.Release();
            }
        }
    }

    static async Task WatchPeerAsync(NetworkStream stream, CancellationTokenSource done)
    {
        try { await stream.ReadAsync(new byte[1], done.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or OperationCanceledException) { }
        finally { done.Cancel(); }
    }

    public void Dispose()
    {
        stopped.Cancel();
        media.Stop();
        pipeLoop.GetAwaiter().GetResult();
        Interlocked.Exchange(ref device, null)?.Dispose();
    }

    [DllImport("kernel32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);
}
