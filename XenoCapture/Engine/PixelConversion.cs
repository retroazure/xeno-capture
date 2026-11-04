using System;
namespace XenoCapture.Engine;
internal static class PixelConversion
{
    // CPU conversion is only used for recording resize and explicit PNG export.
    public static Packet ToBgra(Packet input)
    {
        if (input.Layout == PixelLayout.Bgr32) return input.Retain();
        var output = new Packet(input.Width * input.Height * 4, false, input.Width, input.Height) { Time = input.Time, Duration = input.Duration };
        int w = input.Width, h = input.Height; var src = input.Bytes; var dst = output.Bytes;
        double yScale = input.FullRange ? 1 : 255.0 / 219, chromaScale = input.FullRange ? 1 : 255.0 / 224;
        double red = input.Bt601 ? 1.402 : 1.5748, gu = input.Bt601 ? 0.344136 : 0.187324, gv = input.Bt601 ? 0.714136 : 0.468124, blue = input.Bt601 ? 1.772 : 1.8556;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            int i = y * w + x, uv = w * h + (y / 2) * w + (x & ~1), o = i * 4;
            double luma = (src[i] - (input.FullRange ? 0 : 16)) * yScale, u = (src[uv] - 128) * chromaScale, v = (src[uv + 1] - 128) * chromaScale;
            dst[o] = (byte)Math.Clamp((int)Math.Round(luma + blue * u), 0, 255);
            dst[o + 1] = (byte)Math.Clamp((int)Math.Round(luma - gu * u - gv * v), 0, 255);
            dst[o + 2] = (byte)Math.Clamp((int)Math.Round(luma + red * v), 0, 255); dst[o + 3] = 255;
        }
        return output;
    }
}
