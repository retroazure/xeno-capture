using System.Runtime.CompilerServices;

namespace XenoCapture.Engine;

internal sealed unsafe partial class D3DPreview
{
    // ID3D11DeviceContext slots follow d3d11.h. These calls use subresource zero.
    const int SetPixelResourcesSlot = 8, DrawSlot = 13, MapSubresourceSlot = 14, UnmapSubresourceSlot = 15;
    const int SetRenderTargetsSlot = 33, SetViewportsSlot = 44, ClearRenderTargetViewSlot = 50;

    enum ResourceMap : uint { Read = 1, WriteDiscard = 4 }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    int MapResource(Com resource, ResourceMap access, uint flags, Mapped* mapped) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)context.Slot(MapSubresourceSlot))
            (context.P, resource.P, 0, (uint)access, flags, mapped);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    Mapped MapResource(Com resource, ResourceMap access)
    {
        Mapped mapped;
        Native.Hr(MapResource(resource, access, 0, &mapped));
        return mapped;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void UnmapResource(Com resource) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)context.Slot(UnmapSubresourceSlot))(context.P, resource.P, 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void CopyResource(Com destination, Com source) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)context.Slot(47))(context.P, destination.P, source.P);

    void ClearPixelInputs()
    {
        nint* views = stackalloc nint[2] { 0, 0 };
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)context.Slot(SetPixelResourcesSlot))(context.P, 0, 2, views);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void UnbindRenderTarget() =>
        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)context.Slot(SetRenderTargetsSlot))(context.P, 0, null, 0);

    int GetDeviceRemovalReason() => ((delegate* unmanaged[Stdcall]<nint, int>)device.Slot(39))(device.P);
}
