using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace XenoCapture.Engine;

internal interface IAndroidUsb : IDisposable
{
    void ReadExactly(Span<byte> destination, CancellationToken token);
    void Write(ReadOnlySpan<byte> data, CancellationToken token);
    void Abort();
}

// The device-side ADB protocol over USB, with no host ADB daemon or network transports.
internal sealed class AndroidAdbConnection : IDisposable
{
    internal enum Command : uint
    {
        Connect = 0x4e584e43, Auth = 0x48545541, Open = 0x4e45504f,
        Okay = 0x59414b4f, Write = 0x45545257, Close = 0x45534c43
    }

    const uint ChecksumVersion = 0x01000000, CurrentVersion = 0x01000001;
    const int MaxPayload = 1024 * 1024;
    readonly IAndroidUsb usb;
    readonly CancellationTokenSource stopped = new();
    readonly object writeGate = new();
    readonly ConcurrentDictionary<uint, AdbChannel> channels = new();
    uint version = ChecksumVersion;
    int payloadLimit = 4096, lastChannel;
    Task? reader;

    internal AndroidAdbConnection(IAndroidUsb usb) => this.usb = usb;

    internal static AndroidAdbConnection Connect(CancellationToken token)
    {
        IAndroidUsb? usb;
        while ((usb = AndroidWinUsb.Find()) == null)
        {
            if (token.WaitHandle.WaitOne(150)) token.ThrowIfCancellationRequested();
        }

        var connection = new AndroidAdbConnection(usb);
        try
        {
            connection.Authorize(token);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal void Authorize(CancellationToken token, RSA? key = null)
    {
        using var abort = token.Register(usb.Abort);
        using RSA? ownedKey = key == null ? OpenKey() : null;
        key ??= ownedKey!;
        int challenges = 0;
        Send(Command.Connect, CurrentVersion, MaxPayload, "host::\0"u8, token);
        while (true)
        {
            var packet = Read(token);
            if (packet.Command == Command.Connect && packet.A >= ChecksumVersion && packet.B >= 4096 &&
                packet.Data.AsSpan().StartsWith("device::"u8))
            {
                version = Math.Min(packet.A, CurrentVersion);
                payloadLimit = (int)Math.Min(packet.B, MaxPayload);
                reader = Task.Factory.StartNew(ReadLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                return;
            }
            if (packet.Command != Command.Auth || packet.A != 1 || packet.Data.Length != 20 || challenges == 2)
                throw new InvalidDataException("The Android USB debugging handshake was rejected.");
            if (challenges++ == 0)
                Send(Command.Auth, 2, 0, key.SignHash(packet.Data, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1), token);
            else
                Send(Command.Auth, 3, 0, PublicKey(key), token);
        }
    }

    static RSACng OpenKey()
    {
        // Per Windows user, non-exportable, separate from Android Studio or system ADB.
        const string name = "Xeno Capture Android USB";
        var provider = CngProvider.MicrosoftSoftwareKeyStorageProvider;
        CngKey key;
        try { key = CngKey.Open(name, provider); }
        catch (CryptographicException)
        {
            var parameters = new CngKeyCreationParameters { Provider = provider, KeyUsage = CngKeyUsages.Signing };
            parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None));
            try { key = CngKey.Create(CngAlgorithm.Rsa, name, parameters); }
            catch (CryptographicException) { key = CngKey.Open(name, provider); }
        }
        using (key) return new RSACng(key);
    }

    static byte[] PublicKey(RSA key)
    {
        // Android's wire format is 64 little-endian words, -1/N mod 2^32, N, R^2 mod N, exponent.
        RSAParameters p = key.ExportParameters(false);
        var modulus = new BigInteger(p.Modulus!, isUnsigned: true, isBigEndian: true);
        var exponent = new BigInteger(p.Exponent!, isUnsigned: true, isBigEndian: true);
        uint n = (uint)(modulus & uint.MaxValue);
        uint inverse = 1;
        for (int i = 0; i < 5; i++) inverse = unchecked(inverse * (2 - n * inverse));
        var data = new byte[524];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 64);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), unchecked(0u - inverse));
        modulus.TryWriteBytes(data.AsSpan(8, 256), out _, isUnsigned: true);
        ((BigInteger.One << 4096) % modulus).TryWriteBytes(data.AsSpan(264, 256), out _, isUnsigned: true);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(520), (uint)exponent);
        return Encoding.ASCII.GetBytes(Convert.ToBase64String(data) + " XenoCapture@Windows\0");
    }

    static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        foreach (byte value in data) sum += value;
        return sum;
    }

    (Command Command, uint A, uint B, byte[] Data) Read(CancellationToken token)
    {
        Span<byte> header = stackalloc byte[24];
        usb.ReadExactly(header, token);
        uint command = BinaryPrimitives.ReadUInt32LittleEndian(header);
        uint a = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        uint b = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        if (magic != ~command || size > payloadLimit)
            throw new InvalidDataException("Invalid Android USB packet header.");
        var data = size == 0 ? [] : GC.AllocateUninitializedArray<byte>((int)size);
        usb.ReadExactly(data, token);
        // A modern device can skip the checksum on its first CNXN.
        if (version == ChecksumVersion && (Command)command != Command.Auth && !((Command)command == Command.Connect && a >= CurrentVersion) &&
            checksum != Checksum(data))
            throw new InvalidDataException("Invalid Android USB packet checksum.");
        return ((Command)command, a, b, data);
    }

    void Send(Command command, uint a, uint b, ReadOnlySpan<byte> data, CancellationToken token)
    {
        Span<byte> header = stackalloc byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)command);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], a);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], b);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], version == ChecksumVersion ? Checksum(data) : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], ~(uint)command);
        lock (writeGate)
        {
            stopped.Token.ThrowIfCancellationRequested();
            usb.Write(header, token);
            if (!data.IsEmpty) usb.Write(data, token);
        }
    }

    void ReadLoop()
    {
        try
        {
            while (!stopped.IsCancellationRequested)
            {
                var (command, remote, local, data) = Read(stopped.Token);
                if (command == Command.Open)
                {
                    Send(Command.Close, 0, remote, [], stopped.Token);
                    continue;
                }
                if (!channels.TryGetValue(local, out var channel))
                {
                    if (command == Command.Okay && remote != 0)
                        Send(Command.Close, local, remote, [], stopped.Token);
                    continue;
                }
                if (!channel.Receive(command, remote, data))
                    throw new InvalidDataException("Invalid Android USB stream state.");
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException)
        {
            Stop();
        }
    }

    internal async Task<AdbChannel> OpenAsync(string service, CancellationToken token)
    {
        uint local = (uint)Interlocked.Increment(ref lastChannel);
        var channel = new AdbChannel(this, local);
        if (!channels.TryAdd(local, channel)) throw new IOException("Android USB stream ID collision.");
        try
        {
            Send(Command.Open, local, 0, Encoding.ASCII.GetBytes(service + '\0'), token);
            await channel.WaitUntilOpen(token).ConfigureAwait(false);
            return channel;
        }
        catch
        {
            channel.Dispose();
            throw;
        }
    }

    void Stop()
    {
        stopped.Cancel();
        foreach (var channel in channels.Values) channel.Complete();
    }

    public void Dispose()
    {
        Stop();
        usb.Abort();
        reader?.GetAwaiter().GetResult();
        lock (writeGate) usb.Dispose();
        // A cancelled socket relay may still unwind; leave its cancellation source readable.
    }

    internal sealed class AdbChannel : IDisposable
    {
        readonly AndroidAdbConnection owner;
        readonly uint local;
        readonly TaskCompletionSource<uint> opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly Channel<byte[]> incoming = Channel.CreateBounded<byte[]>(1);
        readonly Channel<bool> acknowledgements = Channel.CreateBounded<bool>(1);
        uint remote;
        int disposed;

        internal AdbChannel(AndroidAdbConnection owner, uint local) => (this.owner, this.local) = (owner, local);

        internal async Task WaitUntilOpen(CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, owner.stopped.Token);
            if (await opened.Task.WaitAsync(linked.Token).ConfigureAwait(false) == 0)
                throw new IOException("The phone refused the Android USB stream.");
        }

        internal async ValueTask<byte[]?> ReadAsync(CancellationToken token)
        {
            return await incoming.Reader.WaitToReadAsync(token).ConfigureAwait(false) &&
                incoming.Reader.TryRead(out var data) ? data : null;
        }

        internal void Ack(CancellationToken token) => owner.Send(Command.Okay, local, remote, [], token);

        internal async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, owner.stopped.Token);
            while (!data.IsEmpty)
            {
                int length = Math.Min(owner.payloadLimit, data.Length);
                owner.Send(Command.Write, local, remote, data.Span[..length], linked.Token);
                if (!await acknowledgements.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false) ||
                    !acknowledgements.Reader.TryRead(out _))
                    throw new IOException("The phone closed the Android USB stream.");
                data = data[length..];
            }
        }

        internal bool Complete()
        {
            opened.TrySetResult(0);
            incoming.Writer.TryComplete();
            acknowledgements.Writer.TryComplete();
            return true;
        }

        internal bool Receive(Command command, uint sender, byte[] data)
        {
            if (Volatile.Read(ref disposed) != 0) return true;
            if (command == Command.Close && (remote == sender || sender == 0 || remote == 0)) return Complete();
            if (sender == 0 || (remote != 0 && remote != sender)) return false;
            if (command == Command.Okay && remote == 0)
            {
                remote = sender;
                return opened.TrySetResult(sender);
            }
            return remote != 0 && (command switch
            {
                Command.Okay => acknowledgements.Writer.TryWrite(true),
                Command.Write => incoming.Writer.TryWrite(data),
                _ => false
            });
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            owner.channels.TryRemove(local, out _);
            Complete();
            if (remote == 0 || owner.stopped.IsCancellationRequested) return;
            try { owner.Send(Command.Close, local, remote, [], owner.stopped.Token); }
            catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException) { }
        }
    }
}
