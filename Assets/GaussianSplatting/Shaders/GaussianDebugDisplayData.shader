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
uint _DisplayDataScale;

static const uint kDisplayPosition = 1;
static const uint kDisplayScale = 2;
static const uint kDisplayRotation = 3;
static const uint kDisplayColor = 4;
static const uint kDisplayOpacity = 5;
static const uint kDisplaySH1 = 6;

half4 frag (v2f i) : SV_Target
{
    uint2 pixelPos = i.vertex.xy / _DisplayDataScale;

    half4 res = 1;
    SplatData splat = LoadSplatDataRaw(pixelPos);
    if (_DisplayMode == kDisplayPosition) res.rgb = splat.pos;
    if (_DisplayMode == kDisplayRotation) res.rgb = splat.rot.rgb;
    if (_DisplayMode == kDisplayScale) res.rgb = splat.scale;
    if (_DisplayMode == kDisplayColor) res.rgb = splat.sh.col;
    if (_DisplayMode == kDisplayOpacity) res.rgb = splat.opacity;
    if (_DisplayMode == kDisplaySH1 + 0) res.rgb = splat.sh.sh1;
    if (_DisplayMode == kDisplaySH1 + 1) res.rgb = splat.sh.sh2;
    if (_DisplayMode == kDisplaySH1 + 2) res.rgb = splat.sh.sh3;
    if (_DisplayMode == kDisplaySH1 + 3) res.rgb = splat.sh.sh4;
    if (_DisplayMode == kDisplaySH1 + 4) res.rgb = splat.sh.sh5;
    if (_DisplayMode == kDisplaySH1 + 5) res.rgb = splat.sh.sh6;
    if (_DisplayMode == kDisplaySH1 + 6) res.rgb = splat.sh.sh7;
    if (_DisplayMode == kDisplaySH1 + 7) res.rgb = splat.sh.sh8;
    if (_DisplayMode == kDisplaySH1 + 8) res.rgb = splat.sh.sh9;
    if (_DisplayMode == kDisplaySH1 + 9) res.rgb = splat.sh.sh10;
    if (_DisplayMode == kDisplaySH1 + 10) res.rgb = splat.sh.sh11;
    if (_DisplayMode == kDisplaySH1 + 11) res.rgb = splat.sh.sh12;
    if (_DisplayMode == kDisplaySH1 + 12) res.rgb = splat.sh.sh13;
    if (_DisplayMode == kDisplaySH1 + 13) res.rgb = splat.sh.sh14;
    if (_DisplayMode == kDisplaySH1 + 14) res.rgb = splat.sh.sh15;
    return res;
}
ENDCG
        }
    }
}
