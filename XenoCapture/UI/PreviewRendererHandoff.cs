using System;
using System.Threading;
using System.Threading.Tasks;
using XenoCapture.Engine;

namespace XenoCapture.UI;

// The outgoing owner detaches the swap chain before publishing the device.
// The incoming owner claims it before touching the immediate context.
internal sealed class PreviewRendererHandoff
{
    D3DPreview? parked;

    internal D3DPreview? Take() => Interlocked.Exchange(ref parked, null);

    internal void Park(D3DPreview renderer)
    {
        if (Interlocked.CompareExchange(ref parked, renderer, null) != null)
            throw new InvalidOperationException("A preview renderer is already parked.");
    }

    // Both hosts have stopped before the capture session releases its device.
    internal Task ReleaseAsync()
    {
        var renderer = Take();
        return renderer == null ? Task.CompletedTask : Task.Run(() =>
        {
            using var apartment = new Apartment();
            renderer.Dispose();
        });
    }
}
