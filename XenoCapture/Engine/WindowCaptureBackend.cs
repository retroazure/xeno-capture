using System;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace XenoCapture.Engine;

internal sealed unsafe class WindowCaptureBackend(GraphicsCaptureItem item) : ICaptureBackend
{
    const DirectXPixelFormat CaptureFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;
    const int BufferCount = 2;
    const int MaxPixels = 3840 * 2160;
    static readonly Guid CaptureItemId = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");
    static readonly Guid DxgiDeviceId = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    static readonly Guid MultithreadId = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");
    static readonly Guid SurfaceAccessId = new("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1");
    static readonly Guid Texture2DId = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    [ComImport, Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(nint window, in Guid iid);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TextureDesc
    {
        public uint Width, Height, MipLevels, ArraySize, Format, Samples, SampleQuality, Usage, BindFlags, CpuAccess, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Mapped
    {
        public nint Data;
        public uint RowPitch, DepthPitch;
    }

    [DllImport("d3d11.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    readonly object signalGate = new();
    readonly AutoResetEvent changed = new(false);
    CancellationToken cancellation;
    WaitHandle[] waits = [];
    volatile bool windowClosed;
    bool disposed, refreshAfterResize;
    Com? device, context, staging;
    IDirect3DDevice? graphicsDevice;
    Direct3D11CaptureFramePool? pool;
    GraphicsCaptureSession? session;
    SizeInt32 size;
    long lastTime;

    public PixelLayout Layout => PixelLayout.Bgr32;
    public int AudioRate => 0;
    public int AudioChannels => 0;
    public bool RequiresExternalShutdown => false;

    internal static GraphicsCaptureItem CreateItem(nint window)
    {
        var factory = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        using var itemReference = new Com(factory.CreateForWindow(window, CaptureItemId));
        return GraphicsCaptureItem.FromAbi(itemReference.P)
            ?? throw new InvalidOperationException("Windows could not open the preview window.");
    }

    public void Open(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        cancellation = token;
        waits = [token.WaitHandle, changed];
        size = ValidateSize(item.Size);
        item.Closed += OnClosed;

        // The worker owns readback. Windows uses the same device from its frame-pool thread.
        Native.CreateD3D11Device(out nint devicePointer, out nint contextPointer);
        device = new(devicePointer);
        context = new(contextPointer);
        using var multithread = context.Query(MultithreadId)
            ?? throw new InvalidOperationException("The graphics driver does not support window capture.");
        ((delegate* unmanaged[Stdcall]<nint, int, int>)multithread.Slot(5))(multithread.P, 1);
        using var dxgi = device.Query(DxgiDeviceId)
            ?? throw new InvalidOperationException("Windows could not initialize window capture.");
        Native.Hr(CreateDirect3D11DeviceFromDXGIDevice(dxgi.P, out nint graphicsPointer));
        using var graphicsReference = new Com(graphicsPointer);
        graphicsDevice = MarshalInterface<IDirect3DDevice>.FromAbi(graphicsReference.P);

        StartSession();
    }

    public Packet? Read()
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (windowClosed) throw new InvalidOperationException("The preview window closed.");
            var frame = pool!.TryGetNextFrame();
            if (frame == null)
            {
                int wait = WaitHandle.WaitAny(waits, refreshAfterResize ? 250 : System.Threading.Timeout.Infinite);
                if (wait == WaitHandle.WaitTimeout && !windowClosed)
                {
                    // A static receiver may not repaint after the frame pool changes size.
                    refreshAfterResize = false;
                    CloseSession();
                    StartSession();
                }
                continue;
            }

            // Discard an older queued frame when Windows already has a newer one.
            var newer = pool.TryGetNextFrame();
            if (newer != null)
            {
                frame.Dispose();
                frame = newer;
            }

            SizeInt32 frameSize;
            using (frame)
            {
                frameSize = frame.ContentSize;
                if (frameSize.Width == size.Width && frameSize.Height == size.Height)
                {
                    refreshAfterResize = false;
                    return ReadPixels(frame);
                }
            }

            if (frameSize.Width <= 0 || frameSize.Height <= 0) return null;
            size = ValidateSize(frameSize);
            staging?.Dispose();
            staging = null;
            pool.Recreate(graphicsDevice!, CaptureFormat, BufferCount, size);
            refreshAfterResize = true;
            return null;
        }
    }

    void StartSession()
    {
        cancellation.ThrowIfCancellationRequested();
        pool = Direct3D11CaptureFramePool.CreateFreeThreaded(graphicsDevice!, CaptureFormat, BufferCount, size);
        pool.FrameArrived += OnFrameArrived;
        session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        session.StartCapture();
    }

    void CloseSession()
    {
        var oldSession = session;
        var oldPool = pool;
        session = null;
        pool = null;
        try { oldSession?.Dispose(); }
        finally
        {
            if (oldPool != null)
            {
                oldPool.FrameArrived -= OnFrameArrived;
                oldPool.Dispose();
            }
        }
    }

    Packet ReadPixels(Direct3D11CaptureFrame frame)
    {
        using var surface = new Com(((IWinRTObject)frame.Surface).NativeObject.GetRef());
        using var access = surface.Query(SurfaceAccessId)
            ?? throw new InvalidOperationException("Windows did not provide a Direct3D window surface.");
        Guid textureId = Texture2DId;
        nint texturePointer;
        Native.Hr(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)access.Slot(3))(access.P, &textureId, &texturePointer));
        using var texture = new Com(texturePointer);
        var target = staging ??= CreateStagingTexture();
        nint contextPointer = context!.P;
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)context.Slot(47))(contextPointer, target.P, texture.P);

        Mapped mapped;
        Native.Hr(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)context.Slot(14))
            (contextPointer, target.P, 0, 1, 0, &mapped));
        try
        {
            int stride = checked(size.Width * 4);
            if (mapped.Data == 0 || mapped.RowPitch < stride)
                throw new InvalidOperationException("Windows returned an invalid window capture buffer.");
            long time = frame.SystemRelativeTime.Ticks;
            if (time <= 0) time = Clock.Now;
            var packet = new Packet(stride * size.Height, width: size.Width, height: size.Height, clearOnReturn: true)
            {
                Time = time,
                Duration = lastTime == 0 ? TimeSpan.TicksPerSecond / 60
                    : Math.Clamp(time - lastTime, TimeSpan.TicksPerMillisecond, TimeSpan.TicksPerSecond / 10)
            };
            lastTime = time;
            var destination = packet.Bytes.AsSpan(0, packet.Length);
            if (mapped.RowPitch == stride)
                new ReadOnlySpan<byte>((void*)mapped.Data, destination.Length).CopyTo(destination);
            else
            {
                for (int row = 0; row < size.Height; row++)
                {
                    var pixels = new ReadOnlySpan<byte>((byte*)mapped.Data + row * (nint)mapped.RowPitch, stride);
                    pixels.CopyTo(destination.Slice(row * stride, stride));
                }
            }
            return packet;
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)context.Slot(15))(contextPointer, target.P, 0);
        }
    }

    Com CreateStagingTexture()
    {
        var description = new TextureDesc
        {
            Width = (uint)size.Width, Height = (uint)size.Height, MipLevels = 1, ArraySize = 1,
            Format = (uint)CaptureFormat, Samples = 1, Usage = 3, CpuAccess = 0x20000
        };
        nint texture;
        Native.Hr(((delegate* unmanaged[Stdcall]<nint, TextureDesc*, nint, nint*, int>)device!.Slot(5))
            (device.P, &description, 0, &texture));
        return new(texture);
    }

    static SizeInt32 ValidateSize(SizeInt32 value)
    {
        if (value.Width is <= 0 or > 3840 || value.Height is <= 0 or > 3840 || (long)value.Width * value.Height > MaxPixels)
            throw new InvalidOperationException("The preview window must be 4K or smaller.");
        return value;
    }

    void OnClosed(GraphicsCaptureItem sender, object args)
    {
        windowClosed = true;
        Signal();
    }

    void OnFrameArrived(Direct3D11CaptureFramePool sender, object args) => Signal();

    void Signal()
    {
        lock (signalGate)
        {
            if (!disposed) changed.Set();
        }
    }

    public void Shutdown() { }

    public void Dispose()
    {
        lock (signalGate)
        {
            if (disposed) return;
            disposed = true;
        }

        try
        {
            try { item.Closed -= OnClosed; }
            finally { CloseSession(); }
        }
        finally
        {
            try { graphicsDevice?.Dispose(); }
            finally
            {
                staging?.Dispose();
                context?.Dispose();
                device?.Dispose();
                changed.Dispose();
            }
        }
    }
}
