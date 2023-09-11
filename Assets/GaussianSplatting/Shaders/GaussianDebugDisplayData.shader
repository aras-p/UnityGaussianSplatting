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

struct v2f
{
    float4 vertex : SV_POSITION;
};

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

/*
Morton interleaving 16x16 group i.e. by 4 bits of coordinates, based on https://twitter.com/rygorous/status/986715358852608000
thread which is simplified version of https://fgiesen.wordpress.com/2009/12/13/decoding-morton-codes/

uint EncodeMorton2D_16x16(uint2 c)
{
    uint t = ((c.y & 0xF) << 8) | (c.x & 0xF); // ----EFGH----ABCD
    t = (t ^ (t << 2)) & 0x3333;               // --EF--GH--AB--CD
    t = (t ^ (t << 1)) & 0x5555;               // -E-F-G-H-A-B-C-D
    return (t | (t >> 7)) & 0xFF;              // --------EAFBGCHD
}

uint2 DecodeMorton2D_16x16(uint t)
{
    t = (t & 0xFF) | ((t & 0xFE) << 7); // -EAFBGCHEAFBGCHD
    t &= 0x5555;                        // -E-F-G-H-A-B-C-D
    t = (t ^ (t >> 1)) & 0x3333;        // --EF--GH--AB--CD
    t = (t ^ (t >> 2)) & 0x0f0f;        // ----EFGH----ABCD
    return uint2(t & 0xF, t >> 8);
}
*/

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
    SplatData splat = LoadSplatData(idx);
    if (_DisplayMode == kDisplayPosition)
    {
        float3 pos = (splat.pos * float3(1,1,-1) - _BoundsMin) / (_BoundsMax - _BoundsMin);
        res.rgb = pos;
    }
    if (_DisplayMode == kDisplayRotation)
    {
        res.rgb = saturate(splat.rot.rgb * 0.5 + 0.5);
    }
    if (_DisplayMode == kDisplayScale)
    {
        res.rgb = saturate(splat.scale * 3.0);
    }
    if (_DisplayMode == kDisplayColor)
    {
        res.rgb = saturate(splat.sh.col * 0.7);
    }
    if (_DisplayMode == kDisplayOpacity)
    {
        res.rgb = saturate(splat.opacity);
    }
    if (_DisplayMode == kDisplaySH1 + 0) res.rgb = saturate(2 * splat.sh.sh1 * 0.5 + 0.5);
    if (_DisplayMode == kDisplaySH1 + 1) res.rgb = saturate(2 * splat.sh.sh2 * 0.5 + 0.5);
    if (_DisplayMode == kDisplaySH1 + 2) res.rgb = saturate(2 * splat.sh.sh3 * 0.5 + 0.5);
    if (_DisplayMode == kDisplaySH1 + 3) res.rgb = saturate(2 * splat.sh.sh4 * 0.5 + 0.5);
    if (_DisplayMode == kDisplaySH1 + 4) res.rgb = saturate(2 * splat.sh.sh5 * 0.5 + 0.5);
    if (_DisplayMode == kDisplaySH1 + 5) res.rgb = saturate(2 * splat.sh.sh6 * 0.5 + 0.5);
    if (_DisplayMode == kDisplaySH1 + 6) res.rgb = saturate(2 * splat.sh.sh7 * 0.5 + 0.5);
    if (_DisplayMode == kDisplaySH1 + 7) res.rgb = saturate(2 * splat.sh.sh8 * 0.5 + 0.5);
    if (_DisplayMode == kDisplaySH1 + 8) res.rgb = saturate(2 * splat.sh.sh9 * 0.5 + 0.5);
    if (_DisplayMode == kDisplaySH1 + 9) res.rgb = saturate(2 * splat.sh.sh10 * 0.5 + 0.5);
    if (_DisplayMode == kDisplaySH1 + 10) res.rgb = saturate(2 * splat.sh.sh11 * 0.5 + 0.5);
    if (_DisplayMode == kDisplaySH1 + 11) res.rgb = saturate(2 * splat.sh.sh12 * 0.5 + 0.5);
    if (_DisplayMode == kDisplaySH1 + 12) res.rgb = saturate(2 * splat.sh.sh13 * 0.5 + 0.5);
    if (_DisplayMode == kDisplaySH1 + 13) res.rgb = saturate(2 * splat.sh.sh14 * 0.5 + 0.5);
    if (_DisplayMode == kDisplaySH1 + 14) res.rgb = saturate(2 * splat.sh.sh15 * 0.5 + 0.5);
    return res;
}
ENDCG
        }
    }
}
