// SPDX-License-Identifier: MIT
Shader "Gaussian Splatting/Render Splats"
{
    Properties
    {
        _SrcBlend("Src Blend", Float) = 8 // OneMinusDstAlpha
        _DstBlend("Dst Blend", Float) = 1 // One
        _ZWrite("ZWrite", Float) = 0 // Off
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            ZWrite [_ZWrite]
            Blend [_SrcBlend] [_DstBlend]
            Cull Off
            
CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc
#pragma multi_compile_local __ SPLAT_REPROJECTED

#include "GaussianSplatting.hlsl"

StructuredBuffer<uint> _OrderBuffer;

struct v2f
{
    half4 col : COLOR0;
    float2 pos : TEXCOORD0;
    nointerpolation uint idx : TEXCOORD1;
    float4 vertex : SV_POSITION;
};

// Separate constant buffer avoids Unity's DX12 stale-uniform bug (IN-99220).
cbuffer SplatGlobalUniforms // match struct SplatGlobalUniforms in C#
{
    uint sgu_transparencyMode;
    uint sgu_frameOffset;
}

uint _SplatSeed;
StructuredBuffer<SplatViewData> _SplatViewData;

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o = (v2f)0;
    if (sgu_transparencyMode == 0)
        instID = _OrderBuffer[instID];
    // combine local splat index with object ID
    o.idx = (instID ^ (_SplatSeed * 0x9e3779b9u)) + sgu_frameOffset;
	SplatViewData view = _SplatViewData[instID];
	float4 centerClipPos = view.pos;
	bool behindCam = centerClipPos.w <= 0;
	if (behindCam)
	{
		o.vertex = asfloat(0x7fc00000); // NaN discards the primitive
	}
	else
	{
		o.col.r = f16tof32(view.color.x >> 16);
		o.col.g = f16tof32(view.color.x);
		o.col.b = f16tof32(view.color.y >> 16);
		o.col.a = f16tof32(view.color.y);

		uint idx = vtxID;
		float2 quadPos = float2(idx&1, (idx>>1)&1) * 2.0 - 1.0;
		quadPos *= 2;

		o.pos = quadPos;

		float2 deltaScreenPos = (quadPos.x * view.axis1 + quadPos.y * view.axis2) * 2 / _ScreenParams.xy;
		o.vertex = centerClipPos;
		o.vertex.xy += deltaScreenPos * centerClipPos.w;

	}
	FlipProjectionIfBackbuffer(o.vertex);
    return o;
}

// Hash Functions for GPU Rendering
// https://jcgt.org/published/0009/03/02/
uint3 pcg3d16(uint3 v)
{
    v = v * 12829u + 47989u;

    v.x += v.y*v.z;
    v.y += v.z*v.x;
    v.z += v.x*v.y;

    v.x += v.y*v.z;
    v.y += v.z*v.x;
    v.z += v.x*v.y;

	v >>= 16u;
    return v;
}

struct FragOutput
{
    half4 color : SV_Target0;
#if SPLAT_REPROJECTED
    float depth : SV_Target1;
#endif
};

FragOutput frag (v2f i)
{
	float power = -dot(i.pos, i.pos);
	half alpha = exp(power);
	alpha = saturate(alpha * i.col.a);

    if (alpha < 1.0/255.0)
        discard;

    if (sgu_transparencyMode == 0)
    {
        i.col.rgb *= alpha;
    }
    else
    {
        // "Hashed Alpha Testing", Wyman, McGuire 2017
        // https://casual-effects.com/research/Wyman2017Hashed/index.html
        uint3 hash = pcg3d16(uint3(uint2(i.vertex.xy), i.idx));
        float cutoff = hash.x * (1.0 / 65536.0);
        if (alpha <= cutoff)
            discard;
        alpha = 1;
    }
    FragOutput res;
    res.color = half4(i.col.rgb, alpha);
#if SPLAT_REPROJECTED
    res.depth = i.vertex.z;
#endif
    return res;
}
ENDCG
        }
    }
}
