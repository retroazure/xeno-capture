using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace XenoCapture.Engine;

// Vtable slots and GUIDs follow the installed Windows SDK (mfobjects.h / mfreadwrite.h).
// Each Com handle owns exactly one native reference; no RCW lifetime or third-party runtime.
internal sealed unsafe class Com(nint pointer) : IDisposable
{
    nint handle = pointer;
    public nint P => Volatile.Read(ref handle) is var value && value != 0 ? value : throw new ObjectDisposedException(nameof(Com));
    public nint Slot(int index) => (*(nint**)P)[index];
    public int Call(int slot) => ((delegate* unmanaged[Stdcall]<nint, int>)Slot(slot))(P);
    public Com? Query(Guid iid) { nint p; int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(0))(P, &iid, &p); return hr < 0 ? null : new(p); }
    public void U32(Guid key, uint value) => Native.Hr(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Slot(21))(P, &key, value));
    public uint U32(Guid key) => Get32(key);
    public uint Get32(Guid key, uint fallback = 0) { uint v; return ((delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)Slot(7))(P, &key, &v) < 0 ? fallback : v; }
    public void U64(Guid key, ulong value) => Native.Hr(((delegate* unmanaged[Stdcall]<nint, Guid*, ulong, int>)Slot(22))(P, &key, value));
    public ulong U64(Guid key) { ulong v; Native.Hr(((delegate* unmanaged[Stdcall]<nint, Guid*, ulong*, int>)Slot(8))(P, &key, &v)); return v; }
    public void Guid(Guid key, Guid value) => Native.Hr(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(24))(P, &key, &value));
    public Guid Guid(Guid key) { Guid v; Native.Hr(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(10))(P, &key, &v)); return v; }
    public void SetUnknown(Guid key, nint value) => Native.Hr(((delegate* unmanaged[Stdcall]<nint, Guid*, nint, int>)Slot(27))(P, &key, value));
    public void String(Guid key, string value) { fixed (char* s = value) Native.Hr(((delegate* unmanaged[Stdcall]<nint, Guid*, char*, int>)Slot(25))(P, &key, s)); }
    public string String(Guid key) { nint p; uint len; Native.Hr(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, uint*, int>)Slot(13))(P, &key, &p, &len)); try { return Marshal.PtrToStringUni(p, (int)len) ?? ""; } finally { Marshal.FreeCoTaskMem(p); } }
    public void Dispose() { nint owned = Interlocked.Exchange(ref handle, 0); if (owned != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)owned)[2])(owned); }
}

internal static class Keys
{
    public static readonly Guid SourceType = new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3"), VideoSource = new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f"), AudioSource = new("14dd9a1c-7cff-41be-b1b9-ba1ac6ecb571");
    public static readonly Guid Name = new("60d0e559-52f8-4fa2-bbce-acdb34a8ec01"), VideoLink = new("58f0aad8-22bf-4f8a-bb3d-d2c4978c6e2f"), AudioLink = new("30da9258-feb9-47a7-a453-763a7a8e1c5f");
    public static readonly Guid Major = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"), Video = new("73646976-0000-0010-8000-00aa00389b71"), Audio = new("73647561-0000-0010-8000-00aa00389b71");
    public static readonly Guid Nv12 = new("3231564e-0000-0010-8000-00aa00389b71"), NominalRange = new("c21b8ee5-b956-4071-8daf-325edf5cab11"), YuvMatrix = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    public static readonly Guid Rgb32 = new("00000016-0000-0010-8000-00aa00389b71"), H264 = new("34363248-0000-0010-8000-00aa00389b71"), Pcm = new("00000001-0000-0010-8000-00aa00389b71"), Aac = new("00001610-0000-0010-8000-00aa00389b71");
    public static readonly Guid Size = new("1652c33d-d6b2-4012-b834-72030849a37d"), Rate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0"), Aspect = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6"), Interlace = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"), Bitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), Stride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6"), Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    public static readonly Guid Channels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a"), SampleRate = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba"), BytesPerSecond = new("1aab75c8-cfef-451c-ab95-ac034b8e1731"), BlockAlign = new("322de230-9eeb-43bd-ab7a-ff412251541d"), Bits = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    public static readonly Guid ReaderDoesNotShutdownSource = new("56b67165-219e-456d-a22e-2d3004c7fe56");
    public static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee"), AdvancedProcessing = new("0f81da2c-b537-4672-a8b2-a681b17307a3"), Hardware = new("a634a91c-822b-41b9-a494-4de4643612b0"), NoThrottle = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f"), Buffer2D = new("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb");
    public const uint VideoStream = 0xFFFFFFFC, AudioStream = 0xFFFFFFFD, AllStreams = 0xFFFFFFFE;
    public static ulong Pair(int a, int b) => ((ulong)(uint)a << 32) | (uint)b;
    public static (int, int) Unpair(ulong value) => ((int)(value >> 32), (int)(value & uint.MaxValue));
}

internal static unsafe class Native
{
    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)] static extern int MFCreateAttributes(out nint attributes, uint count);
    [DllImport("mfplat.dll", ExactSpelling = true)] static extern int MFCreateMediaType(out nint mediaType);
    [DllImport("mfplat.dll", ExactSpelling = true)] static extern int MFCreateSample(out nint sample);
    [DllImport("mfplat.dll", ExactSpelling = true)] static extern int MFCreateMemoryBuffer(uint length, out nint buffer);
    [DllImport("mfplat.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int MFCreateFile(uint access, uint open, uint flags, string path, out nint stream);
    [DllImport("mf.dll", ExactSpelling = true)] public static extern int MFEnumDeviceSources(nint attributes, out nint sources, out uint count);
    [DllImport("mf.dll", ExactSpelling = true)] static extern int MFCreateDeviceSource(nint attributes, out nint source);
    [DllImport("mfreadwrite.dll", ExactSpelling = true)] static extern int MFCreateSourceReaderFromMediaSource(nint source, nint attributes, out nint reader);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern int MFCreateSinkWriterFromURL(string url, nint stream, nint attributes, out nint writer);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern int MFCreateSourceReaderFromURL(string url, nint attributes, out nint reader);
    [DllImport("ole32.dll")] public static extern int CoInitializeEx(nint reserved, uint model);
    [DllImport("ole32.dll")] public static extern void CoUninitialize();

    [DllImport("ole32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, out nint instance);

    internal static int CreateAudioEndpointEnumerator(out nint instance)
    {
        Guid deviceEnumerator = new("bcde0395-e52f-467c-8e3d-c4579291692e");
        Guid interfaceId = new("a95664d2-9614-4f35-a746-de8db63617e6");
        const uint inProcessServer = 1;
        return CoCreateInstance(ref deviceEnumerator, 0, inProcessServer, ref interfaceId, out instance);
    }

    [DllImport("d3d11.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern int D3D11CreateDevice(nint adapter, uint driver, nint software, uint flags, nint levels,
        uint levelCount, uint sdk, out nint device, nint featureLevel, out nint context);

    internal static void CreateD3D11Device(out nint device, out nint context)
    {
        const uint hardware = 1, bgraSupport = 0x20, sdkVersion = 7;
        Hr(D3D11CreateDevice(0, hardware, 0, bgraSupport, 0, 0, sdkVersion, out device, 0, out context));
    }

    public static void Hr(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    public static Com Attributes() { Hr(MFCreateAttributes(out var p, 8)); return new(p); }
    public static Com Type() { Hr(MFCreateMediaType(out var p)); return new(p); }
    public static Com Source(Com attrs) { Hr(MFCreateDeviceSource(attrs.P, out var p)); return new(p); }
    public static Com Reader(Com source, Com attrs) { Hr(MFCreateSourceReaderFromMediaSource(source.P, attrs.P, out var p)); return new(p); }
    public static Com FileReader(string path, Com? attributes = null) { Hr(MFCreateSourceReaderFromURL(path, attributes?.P ?? 0, out var p)); return new(p); }
    public static Com CreateRecordingStream(string path)
    {
        const uint readWrite = 3, failIfExists = 1;
        int hr = MFCreateFile(readWrite, failIfExists, 0, path, out var stream);
        if (hr is unchecked((int)0x80070050) or unchecked((int)0x800700B7))
            throw new System.IO.IOException("A recording already exists at the destination.");
        Hr(hr);
        return new(stream);
    }
    public static Com Writer(string path, Com stream, Com attrs)
    {
        // The path selects the container. Media Foundation writes to the already-created stream.
        Hr(MFCreateSinkWriterFromURL(path, stream.P, attrs.P, out var writer));
        return new(writer);
    }
    public static Com Sample(ReadOnlySpan<byte> bytes, long time, long duration)
    {
        Hr(MFCreateSample(out var sp)); var sample = new Com(sp);
        try
        {
            Hr(MFCreateMemoryBuffer((uint)bytes.Length, out var bp)); using var buffer = new Com(bp);
            nint data; uint max, length;
            Hr(((delegate* unmanaged[Stdcall]<nint, nint*, uint*, uint*, int>)buffer.Slot(3))(bp, &data, &max, &length));
            try { bytes.CopyTo(new Span<byte>((void*)data, bytes.Length)); } finally { Hr(buffer.Call(4)); }
            Hr(((delegate* unmanaged[Stdcall]<nint, uint, int>)buffer.Slot(6))(bp, (uint)bytes.Length));
            Hr(((delegate* unmanaged[Stdcall]<nint, nint, int>)sample.Slot(42))(sp, bp));
            Hr(((delegate* unmanaged[Stdcall]<nint, long, int>)sample.Slot(36))(sp, time));
            Hr(((delegate* unmanaged[Stdcall]<nint, long, int>)sample.Slot(38))(sp, duration));
            return sample;
        }
        catch { sample.Dispose(); throw; }
    }
    public static Com? MediaType(Com reader, uint stream, uint index, bool current = false)
    {
        nint p;
        int hr = current ? ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)reader.Slot(6))(reader.P, stream, &p)
            : ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)reader.Slot(5))(reader.P, stream, index, &p);
        if (hr == unchecked((int)0xC00D36B9)) return null; // MF_E_NO_MORE_TYPES only.
        Hr(hr); return new(p);
    }
    public static void Select(Com reader, uint stream, bool selected) => Hr(((delegate* unmanaged[Stdcall]<nint, uint, int, int>)reader.Slot(4))(reader.P, stream, selected ? 1 : 0));
    public static void SetType(Com reader, uint stream, Com type) => Hr(((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)reader.Slot(7))(reader.P, stream, 0, type.P));
    public static void RequestSample(Com reader, uint stream) =>
        Hr(((delegate* unmanaged[Stdcall]<nint, uint, uint, nint, nint, nint, nint, int>)reader.Slot(9))(reader.P, stream, 0, 0, 0, 0, 0));
    public static int FlushReader(Com reader, uint stream) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, int>)reader.Slot(10))(reader.P, stream);
    public static Com? Read(Com reader, uint stream, out uint flags, out long timestamp)
    {
        uint actual, f; long t; nint p;
        Hr(((delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, uint*, long*, nint*, int>)reader.Slot(9))(reader.P, stream, 0, &actual, &f, &t, &p));
        flags = f; timestamp = t; return p == 0 ? null : new(p);
    }
    public static uint AddStream(Com writer, Com type) { uint index; Hr(((delegate* unmanaged[Stdcall]<nint, nint, uint*, int>)writer.Slot(3))(writer.P, type.P, &index)); return index; }
    public static void SetInput(Com writer, uint stream, Com type) => Hr(((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)writer.Slot(4))(writer.P, stream, type.P, 0));
    public static void Write(Com writer, uint stream, Com sample) => Hr(((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)writer.Slot(6))(writer.P, stream, sample.P));
}
internal sealed class Apartment : IDisposable
{
    readonly int result = Native.CoInitializeEx(0, 0);
    public Apartment() { Native.Hr(result); }
    public void Dispose() { if (result >= 0) Native.CoUninitialize(); }
}



