using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using XenoCapture.Engine;

namespace XenoCapture.UI;

// The outgoing owner detaches its swap chain before handing off the device.
internal sealed class PreviewRendererHandoff
{
    readonly object gate = new();
    D3DPreview? parked;
    Task<D3DPreview?>? warming;

    internal void Warm()
    {
        lock (gate)
        {
            if (parked == null && warming == null) warming = Task.Run(CreateRenderer);
        }
    }

    static D3DPreview? CreateRenderer()
    {
        try
        {
            using var apartment = new Apartment();
            return new D3DPreview(0, 1, 1, deferTarget: true);
        }
        catch (COMException)
        {
            // The display host will retry on demand and report a persistent failure.
            return null;
        }
    }

    (D3DPreview? Renderer, Task<D3DPreview?>? Warmup) Claim()
    {
        lock (gate)
        {
            var result = (parked, warming);
            parked = null;
            warming = null;
            return result;
        }
    }

    internal D3DPreview? Take()
    {
        var (renderer, warmup) = Claim();
        return renderer ?? warmup?.GetAwaiter().GetResult();
    }

    internal void Park(D3DPreview renderer)
    {
        lock (gate)
        {
            if (parked != null || warming != null)
                throw new InvalidOperationException("A preview renderer is already prepared.");
            parked = renderer;
        }
    }

    internal Task ReleaseAsync()
    {
        var (renderer, warmup) = Claim();
        if (renderer == null && warmup == null) return Task.CompletedTask;
        return Task.Run(async () =>
        {
            var owned = renderer ?? await warmup!.ConfigureAwait(false);
            if (owned == null) return;
            using var apartment = new Apartment();
            owned.Dispose();
        });
    }
}
