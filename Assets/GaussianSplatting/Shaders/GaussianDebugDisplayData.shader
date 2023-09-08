Shader "Gaussian Splatting/Debug/Display Data"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Overlay" }

        Pass
        {
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off
            
CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute

#include "GaussianSplatting.hlsl"

struct InputSplat
{
    float3 pos;
    float3 nor;
    float3 sh0;
    float3 sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, sh10, sh11, sh12, sh13, sh14, sh15;
    float opacity;
    float3 scale;
    float4 rot;
};
StructuredBuffer<InputSplat> _DataBuffer;

struct v2f
{
    float4 vertex : SV_POSITION;
};

float _SplatSize;

v2f vert (uint vtxID : SV_VertexID)
{
    v2f o;

	// two bits per vertex index to result in 0,1,2,1,3,2 from lowest:
	// 0b1011'0110'0100
	uint quadIndices = 0xB64;
	uint idx = quadIndices >> (vtxID * 2);
    float2 quadPos = float2(idx&1, (idx>>1)&1) * 2.0 - 1.0;

	o.vertex = float4(quadPos, 1, 1);
    return o;
}

uint _SplatCount;
uint _DisplayMode;

float3 _BoundsMin;
float3 _BoundsMax;

static const uint kDisplayPosition = 1;
static const uint kDisplayScale = 2;
static const uint kDisplayRotation = 3;
static const uint kDisplayColor = 4;
static const uint kDisplayOpacity = 5;
static const uint kDisplaySH1 = 6;

static const float SH_C0 = 0.2820948;

half4 frag (v2f i) : SV_Target
{
    uint2 pixelPos = i.vertex.xy;
    uint cols = 512;
    if (_ScreenParams.x > 1024)
        cols = 1024;
    if (_ScreenParams.x > 2048)
        cols = 2048;

    if (pixelPos.x >= cols)
        return 0;
    uint idx = pixelPos.y * cols + pixelPos.x;
    if (idx >= _SplatCount)
        return 0;

    half4 res = 1;
    InputSplat splat = _DataBuffer[idx];
    if (_DisplayMode == kDisplayPosition)
    {
        float3 pos = (splat.pos * float3(1,1,-1) - _BoundsMin) / (_BoundsMax - _BoundsMin);
        res.rgb = pos;
    }
    if (_DisplayMode == kDisplayRotation)
    {
        float4 rot = normalize(splat.rot.yzwx);
        res.rgb = saturate(rot.rgb * 0.5 + 0.5);
    }
    if (_DisplayMode == kDisplayScale)
    {
        float3 scl = abs(exp(splat.scale));
        res.rgb = saturate(scl * 3.0);
    }
    if (_DisplayMode == kDisplayColor)
    {
        res.rgb = saturate((SH_C0 * splat.sh0 + 0.5) * 0.7);
    }
    if (_DisplayMode == kDisplayOpacity)
    {
        res.rgb = saturate(Sigmoid(splat.opacity));
    }
    if (_DisplayMode == kDisplaySH1 + 0) res.rgb = saturate(2 * splat.sh1 * 0.5 + 0.5);
    if (_DisplayMode == kDisplaySH1 + 1) res.rgb = saturate(2 * splat.sh2 * 0.5 + 0.5);
    if (_DisplayMode == kDisplaySH1 + 2) res.rgb = saturate(2 * splat.sh3 * 0.5 + 0.5);
    if (_DisplayMode == kDisplaySH1 + 3) res.rgb = saturate(2 * splat.sh4 * 0.5 + 0.5);
    if (_DisplayMode == kDisplaySH1 + 4) res.rgb = saturate(2 * splat.sh5 * 0.5 + 0.5);
    if (_DisplayMode == kDisplaySH1 + 5) res.rgb = saturate(2 * splat.sh6 * 0.5 + 0.5);
    if (_DisplayMode == kDisplaySH1 + 6) res.rgb = saturate(2 * splat.sh7 * 0.5 + 0.5);
    if (_DisplayMode == kDisplaySH1 + 7) res.rgb = saturate(2 * splat.sh8 * 0.5 + 0.5);
    if (_DisplayMode == kDisplaySH1 + 8) res.rgb = saturate(2 * splat.sh9 * 0.5 + 0.5);
    if (_DisplayMode == kDisplaySH1 + 9) res.rgb = saturate(2 * splat.sh10 * 0.5 + 0.5);
    if (_DisplayMode == kDisplaySH1 + 10) res.rgb = saturate(2 * splat.sh11 * 0.5 + 0.5);
    if (_DisplayMode == kDisplaySH1 + 11) res.rgb = saturate(2 * splat.sh12 * 0.5 + 0.5);
    if (_DisplayMode == kDisplaySH1 + 12) res.rgb = saturate(2 * splat.sh13 * 0.5 + 0.5);
    if (_DisplayMode == kDisplaySH1 + 13) res.rgb = saturate(2 * splat.sh14 * 0.5 + 0.5);
    if (_DisplayMode == kDisplaySH1 + 14) res.rgb = saturate(2 * splat.sh15 * 0.5 + 0.5);
    return res;
}
ENDCG
        }
    }
}
