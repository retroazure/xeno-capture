using System;
using System.Diagnostics;

namespace XenoCapture.Engine;

internal static unsafe class AudioSessionVolume
{
    static readonly Guid SessionManagerId = new("77aa99a0-1bd6-484f-8bc7-2c654c9a9b6f");
    static readonly Guid SessionControlId = new("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d");
    static readonly Guid VolumeId = new("87ce5498-68d6-44e5-9215-6da47ef883d8");

    internal static float? Access(Process process, float? requestedLevel)
    {
        // Called on a worker. Only the owned helper's playback sessions are eligible.
        if (Native.CoInitializeEx(0, 0) < 0) return null;
        try
        {
            if (Native.CreateAudioEndpointEnumerator(out nint endpointPointer) < 0) return null;
            using var endpoints = new Com(endpointPointer);
            const int render = 0;
            const uint active = 1;
            nint collectionPointer;
            if (((delegate* unmanaged[Stdcall]<nint, int, uint, nint*, int>)endpoints.Slot(3))
                (endpoints.P, render, active, &collectionPointer) < 0) return null;
            using var collection = new Com(collectionPointer);
            uint count;
            if (((delegate* unmanaged[Stdcall]<nint, uint*, int>)collection.Slot(3))(collection.P, &count) < 0) return null;

            float? level = null;
            for (uint i = 0; i < count; i++)
            {
                nint devicePointer;
                if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)collection.Slot(4))
                    (collection.P, i, &devicePointer) < 0) continue;
                using var device = new Com(devicePointer);
                float? sessionLevel = AccessDevice(device, process, requestedLevel);
                level ??= sessionLevel;
                if (requestedLevel == null && level != null) break;
            }
            return level;
        }
        finally { Native.CoUninitialize(); }
    }

    static float? AccessDevice(Com device, Process process, float? requestedLevel)
    {
        Guid managerId = SessionManagerId;
        nint managerPointer;
        if (((delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)device.Slot(3))
            (device.P, &managerId, 23, 0, &managerPointer) < 0) return null;
        using var manager = new Com(managerPointer);
        nint listPointer;
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)manager.Slot(5))(manager.P, &listPointer) < 0) return null;
        using var list = new Com(listPointer);
        int count;
        if (((delegate* unmanaged[Stdcall]<nint, int*, int>)list.Slot(3))(list.P, &count) < 0) return null;

        float? level = null;
        for (int i = 0; i < count; i++)
        {
            nint sessionPointer;
            if (((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)list.Slot(4))(list.P, i, &sessionPointer) < 0) continue;
            using var session = new Com(sessionPointer);
            using var control = session.Query(SessionControlId);
            uint processId;
            if (control == null || ((delegate* unmanaged[Stdcall]<nint, uint*, int>)control.Slot(14))
                (control.P, &processId) != 0 || processId != process.Id || process.HasExited) continue;
            int state;
            if (((delegate* unmanaged[Stdcall]<nint, int*, int>)session.Slot(3))(session.P, &state) < 0 || state != 1)
                continue; // The playback session must already be active.
            using var volume = session.Query(VolumeId);
            if (volume == null) continue;
            if (requestedLevel is float requested)
            {
                if (((delegate* unmanaged[Stdcall]<nint, float, nint, int>)volume.Slot(3))(volume.P, requested, 0) >= 0)
                    level = requested;
            }
            else
            {
                float current;
                if (((delegate* unmanaged[Stdcall]<nint, float*, int>)volume.Slot(4))(volume.P, &current) >= 0)
                    return current;
            }
        }
        return level;
    }
}
