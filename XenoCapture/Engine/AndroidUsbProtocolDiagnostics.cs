using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XenoCapture.Engine;

internal static class AndroidUsbProtocolDiagnostics
{
    internal static async Task Run(AndroidUsbCaptureBackend backend)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        using var key = RSA.Create(2048);
        var phone = new FakePhone(key);
        var connection = new AndroidAdbConnection(phone);
        try
        {
            connection.Authorize(deadline.Token, key);
            backend.PreviewBridge!.Attach(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        var preview = backend.CreatePreviewProcess("Xeno USB transport diagnostic");
        preview.ArgumentList.Remove("--no-audio");
        preview.ArgumentList.Add("--audio-codec=raw");
        using var process = backend.PreviewJob!.Start(preview);
        await phone.ShellStarted.Task.WaitAsync(deadline.Token);
        using (var outside = new TcpClient(AddressFamily.InterNetwork))
        {
            await outside.ConnectAsync(IPAddress.Loopback, backend.PreviewBridge!.Port, deadline.Token);
            try
            {
                await outside.GetStream().WriteAsync("000chost:version"u8.ToArray(), deadline.Token);
                if (await outside.GetStream().ReadAsync(new byte[4], deadline.Token) != 0)
                    throw new InvalidOperationException("An outside process received a response on the active media relay.");
            }
            catch (IOException error) when (error.InnerException is SocketException) { }
        }
        phone.ReleaseMedia.Set();
        var reachedMedia = phone.MediaAcknowledged.Task;
        if (await Task.WhenAny(reachedMedia, process.WaitForExitAsync(deadline.Token)).WaitAsync(deadline.Token) != reachedMedia)
            throw new InvalidOperationException($"scrcpy exited before using the secure USB media path ({phone.Stage}; exit {process.ExitCode}).");
        await reachedMedia;
        string helper = Path.Combine(backend.SessionHelperDirectory!, "scrcpy-server");
        if (!SHA256.HashData(File.ReadAllBytes(helper)).AsSpan().SequenceEqual(phone.PushedHash))
            throw new InvalidOperationException("The USB sync channel did not send the verified scrcpy server.");
        if (!phone.SignatureValid || !phone.PublicKeySent)
            throw new InvalidOperationException("USB debugging approval did not receive the expected signature and public key.");
        if (!process.HasExited) process.Kill();
        await process.WaitForExitAsync(deadline.Token);
    }

    sealed class FakePhone(RSA key) : IAndroidUsb
    {
        const uint Connect = 0x4e584e43, Auth = 0x48545541, Open = 0x4e45504f,
            Okay = 0x59414b4f, WritePacket = 0x45545257, Close = 0x45534c43;
        const uint SyncId = 31, ShellId = 32, VideoId = 33, AudioId = 34;
        static readonly byte[] Challenge = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20];
        readonly BlockingCollection<byte[]> pending = new();
        readonly MemoryStream pushed = new();
        readonly byte[] hostHeader = new byte[24];
        string? mediaAddress;
        uint syncLocal, videoLocal, audioLocal;
        bool expectingBody, videoReceived, audioReceived;
        internal string Stage { get; private set; } = "connect";
        internal bool SignatureValid { get; private set; }
        internal bool PublicKeySent { get; private set; }
        internal byte[] PushedHash => SHA256.HashData(pushed.ToArray());
        internal ManualResetEventSlim ReleaseMedia { get; } = new();
        internal TaskCompletionSource ShellStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource MediaAcknowledged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReadExactly(Span<byte> destination, CancellationToken token)
        {
            if (destination.IsEmpty) return;
            try
            {
                var transfer = pending.Take(token);
                if (transfer.Length != destination.Length) throw new InvalidDataException("Unexpected test transfer size.");
                transfer.CopyTo(destination);
            }
            catch (InvalidOperationException) { throw new OperationCanceledException(token); }
        }

        public void Write(ReadOnlySpan<byte> data, CancellationToken token)
        {
            if (expectingBody)
            {
                expectingBody = false;
                if (data.Length != Field(hostHeader, 12)) throw new InvalidDataException("Incorrect outbound USB packet length.");
                Handle(data);
                return;
            }
            if (data.Length != hostHeader.Length || Field(data, 20) != ~Field(data, 0))
                throw new InvalidDataException("Incorrect outbound USB packet header.");
            data.CopyTo(hostHeader);
            expectingBody = Field(data, 12) != 0;
            if (!expectingBody) Handle([]);
        }

        void Handle(ReadOnlySpan<byte> body)
        {
            uint verb = Field(hostHeader, 0), local = Field(hostHeader, 4), remote = Field(hostHeader, 8);
            switch (verb)
            {
                case Connect:
                    Stage = "authorize";
                    Reply(Auth, 1, 0, Challenge);
                    break;
                case Auth when local == 2:
                    SignatureValid = key.VerifyHash(Challenge, body, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
                    Reply(Auth, 1, 0, Challenge);
                    break;
                case Auth when local == 3:
                    string adbKey = Encoding.ASCII.GetString(body).Split(' ')[0];
                    byte[] raw = Convert.FromBase64String(adbKey);
                    byte[] modulus = key.ExportParameters(false).Modulus!;
                    Array.Reverse(modulus);
                    PublicKeySent = raw.Length == 524 && raw.AsSpan(8, 256).SequenceEqual(modulus);
                    Stage = "waiting for helper";
                    Reply(Connect, 0x01000001, 1024 * 1024, "device::\0"u8);
                    break;
                case Open:
                    OpenService(local, Encoding.ASCII.GetString(body).TrimEnd('\0'));
                    break;
                case WritePacket when remote == SyncId:
                    Reply(Okay, SyncId, local, []);
                    if (body.StartsWith("DATA"u8)) pushed.Write(body[8..]);
                    if (body.StartsWith("DONE"u8))
                    {
                        Stage = "pushed helper";
                        Reply(WritePacket, SyncId, syncLocal, "OKAY\0\0\0\0"u8);
                    }
                    break;
                case Okay when remote == VideoId:
                    videoReceived = true;
                    if (audioReceived) MediaAcknowledged.TrySetResult();
                    break;
                case Okay when remote == AudioId:
                    audioReceived = true;
                    if (videoReceived) MediaAcknowledged.TrySetResult();
                    break;
            }
        }

        void OpenService(uint local, string service)
        {
            if (service == "sync:")
            {
                Stage = "pushing helper";
                syncLocal = local;
                Reply(Okay, SyncId, local, []);
            }
            else if (service.StartsWith("shell:CLASSPATH=/data/local/tmp/scrcpy-server.jar ", StringComparison.Ordinal) &&
                service.Contains(" control=false", StringComparison.Ordinal) &&
                service.Contains(" tunnel_forward=true", StringComparison.Ordinal) &&
                service.Contains(" audio_codec=raw", StringComparison.Ordinal))
            {
                string scid = Array.Find(service.Split(' '), static arg => arg.StartsWith("scid=", StringComparison.Ordinal))!;
                mediaAddress = "localabstract:scrcpy_" + scid[5..];
                Stage = "waiting for video connection";
                Reply(Okay, ShellId, local, []);
                ShellStarted.TrySetResult();
            }
            else if (mediaAddress != null && service == mediaAddress && videoLocal == 0)
            {
                if (!ReleaseMedia.Wait(TimeSpan.FromSeconds(5)))
                    throw new IOException("The simulated phone timed out waiting for the unauthorized peer probe.");
                videoLocal = local;
                Stage = "waiting for audio connection";
                Reply(Okay, VideoId, local, []);
                byte[] metadata = new byte[69];
                "Xeno USB test"u8.CopyTo(metadata.AsSpan(1));
                "h264"u8.CopyTo(metadata.AsSpan(65));
                Reply(WritePacket, VideoId, local, metadata);
            }
            else if (mediaAddress != null && service == mediaAddress && audioLocal == 0)
            {
                audioLocal = local;
                Stage = "waiting for media acknowledgements";
                Reply(Okay, AudioId, local, []);
                Reply(WritePacket, AudioId, local, "\0raw"u8);
            }
            else Reply(Close, 0, local, []);
        }

        void Reply(uint verb, uint a, uint b, ReadOnlySpan<byte> body)
        {
            byte[] header = new byte[24];
            BinaryPrimitives.WriteUInt32LittleEndian(header, verb);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), a);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), b);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), body.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), ~verb);
            pending.Add(header);
            if (!body.IsEmpty) pending.Add(body.ToArray());
        }

        static uint Field(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        public void Abort() => pending.CompleteAdding();
        public void Dispose() { pending.Dispose(); pushed.Dispose(); ReleaseMedia.Dispose(); }
    }
}
