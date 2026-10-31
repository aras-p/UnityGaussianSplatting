// SPDX-License-Identifier: MIT
Shader "Hidden/Gaussian Splatting/Composite"
{
CGINCLUDE
#pragma require compute
#pragma use_dxc
#include "UnityCG.cginc"

struct v2f
{
    float4 vertex : SV_POSITION;
};

v2f vert (uint vtxID : SV_VertexID)
{
    v2f o;
    float2 quadPos = float2(vtxID&1, (vtxID>>1)&1) * 4.0 - 1.0;
	o.vertex = float4(quadPos, 1, 1);
    return o;
}

Texture2D _GaussianSplatRT;

half4 frag (v2f i) : SV_Target
{
    half4 col = _GaussianSplatRT.Load(int3(i.vertex.xy, 0));
    col.rgb = GammaToLinearSpace(col.a != 0 ? col.rgb/col.a : col.rgb);
    return float4(col.rgb, col.a);
}
ENDCG
    SubShader
    {
        ZWrite Off
        ZTest Always
        Cull Off

        // Composite splats
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
CGPROGRAM
#pragma vertex vert
#pragma fragment frag
ENDCG
        }

        // Stochastic temporal: average premultiplied splat color and coverage,
        // with optional reprojection
        Pass
        {
            Blend Off
CGPROGRAM
#pragma vertex vert
#pragma fragment fragAccumulate
#pragma multi_compile_local __ SPLAT_REPROJECTED
Texture2D _SplatHistory;

#if defined(SPLAT_REPROJECTED)
Texture2D _GaussianSplatDepthRT;
SamplerState linear_clamp_sampler;
float4x4 _SplatInvViewProjection;
float4x4 _SplatPreviousViewProjection;
float3 _SplatCameraPosition;
uint _SplatOrthographic;

// use YCoCg for history rejection, similar to SteadySplats
float4 HistoryColor(float4 color)
{
    return float4(dot(color.rgb, float3(0.25, 0.5, 0.25)),
        (color.r - color.b) * 0.5, dot(color.rgb, float3(-0.25, 0.5, -0.25)), color.a);
}

half4 ReprojectHistory(float2 pixel, half4 current)
{
    uint width, height;
    _GaussianSplatRT.GetDimensions(width, height);
    float2 size = float2(width, height);
    float2 clipXY = pixel / size * 2.0 - 1.0;
    clipXY.y = -clipXY.y;

    float depth = _GaussianSplatDepthRT.Load(int3(pixel, 0)).r;
    float4 world;
    if (current.a == 0)
    {
        // we are in the background pixel; there's no depth.
        // for perspective case, reproject rays at infinity;
        // skip reprojection for orthographic background pixels.
        if (_SplatOrthographic != 0)
            return current;
        world = mul(_SplatInvViewProjection, float4(clipXY, 0.5, 1));
        world.xyz -= _SplatCameraPosition * world.w;
        world.w = 0;
    }
    else
        world = mul(_SplatInvViewProjection, float4(clipXY, depth, 1));

    float4 previousClip = mul(_SplatPreviousViewProjection, world);
    if (previousClip.w <= 0)
        return current; // no usable history for perspective samples behind old camera
    float2 uv = previousClip.xy / previousClip.w * 0.5 + 0.5;
    uv.y = 1.0 - uv.y;

    // keep bilinear footprint inside history instead of clamping edge texels
    if (any(uv < 0.5 / size) || any(uv > 1.0 - 0.5 / size))
        return current;

    half4 history = _SplatHistory.SampleLevel(linear_clamp_sampler, uv, 0);

    // motion adaptive soft history rejection similar to SteadySplats appendix 8
    // https://arxiv.org/html/2610.05576v2
    // we don't do spatial resampling bit though
    float4 mean = 0, secondMoment = 0;
    for (int y = -1; y <= 1; ++y)
    {
        for (int x = -1; x <= 1; ++x)
        {
            int2 p = clamp(int2(pixel) + int2(x, y), int2(0, 0), int2(width, height) - 1);
            float4 color = HistoryColor(_GaussianSplatRT.Load(int3(p, 0)));
            mean += color;
            secondMoment += color * color;
        }
    }
    mean /= 9.0;
    float4 deviation = sqrt(max(0, secondMoment / 9.0 - mean * mean));
    float velocity = length(uv * size - pixel);

    // confidence width from paper: 4 stddev at zero pixel motion, reduced to 1 at >=4 pixels of motion
    float gamma = lerp(4.0, 1.0, saturate(velocity / 4.0));
    float4 extent = max(gamma * deviation, float4(0.01, 0.005, 0.005, 0.01));
    float4 distance = abs(HistoryColor(history) - mean) / extent;
    float mismatch = max(max(distance.x, distance.y), max(distance.z, distance.w));
    float retention = 0.7 / max(1.0, mismatch);
    return lerp(current, history, retention);
}
#else
float _SplatFrameWeight;
#endif

half4 fragAccumulate(v2f i) : SV_Target
{
    half4 current = _GaussianSplatRT.Load(int3(i.vertex.xy, 0));
#if defined(SPLAT_REPROJECTED)
    return ReprojectHistory(i.vertex.xy, current);
#else
    // at reset weight=1, just copy without reading uninitialized history
    if (_SplatFrameWeight == 1.0)
        return current;
    half4 history = _SplatHistory.Load(int3(i.vertex.xy, 0));
    return lerp(history, current, _SplatFrameWeight);
#endif
}
ENDCG
        }
    }
}
