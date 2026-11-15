Texture2D image : register(t0);
SamplerState linearClamp : register(s0);
cbuffer Parameters : register(b0) { float2 SourceSize; };

void CubicAxis(float texel, out float3 positions, out float3 weights)
{
    float origin = floor(texel);
    float t = texel - origin;
    float t2 = t * t, t3 = t2 * t;
    float w0 = -.5 * t + t2 - .5 * t3;
    float w1 = 1 - 2.5 * t2 + 1.5 * t3;
    float w2 = .5 * t + 2 * t2 - 1.5 * t3;
    float w3 = -.5 * t2 + .5 * t3;
    // The two positive center weights can be combined by the bilinear sampler.
    weights = float3(w0, w1 + w2, w3);
    positions = origin + float3(-.5, .5 + w2 / weights.y, 2.5);
}

float4 Scale(float4 position : SV_POSITION, float2 uv : TEXCOORD) : SV_TARGET
{
    float2 texel = uv * SourceSize - .5;
    float3 xs, ys, wx, wy;
    CubicAxis(texel.x, xs, wx);
    CubicAxis(texel.y, ys, wy);
    xs /= SourceSize.x;
    ys /= SourceSize.y;
    float3 color = 0;
    [unroll] for (int y = 0; y < 3; y++)
        [unroll] for (int x = 0; x < 3; x++)
            color += image.SampleLevel(linearClamp, float2(xs[x], ys[y]), 0).rgb * wx[x] * wy[y];
    return float4(saturate(color), 1);
}
