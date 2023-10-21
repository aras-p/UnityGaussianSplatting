// SPDX-License-Identifier: MIT
Shader "Gaussian Splatting/Render Splats"
{
	Properties
	{
		_SrcBlend("Src Blend", Float) = 8 // OneMinusDstAlpha
		_DstBlend("Dst Blend", Float) = 1 // One
		_ZWrite("ZWrite", Float) = 0  // Off
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

#include "GaussianSplatting.hlsl"

StructuredBuffer<uint> _OrderBuffer;

struct v2f
{
    half4 col : COLOR0;
    float2 pos : TEXCOORD0;
	half idx : TEXCOORD1;
    float4 vertex : SV_POSITION;
};

StructuredBuffer<SplatViewData> _SplatViewData;
ByteAddressBuffer _SplatSelectedBits;
uint _SplatBitsValid;

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o = (v2f)0;
    instID = _OrderBuffer[instID];
	o.idx = instID & 63;
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

		// is this splat selected?
		if (_SplatBitsValid)
		{
			uint wordIdx = instID / 32;
			uint bitIdx = instID & 31;
			uint selVal = _SplatSelectedBits.Load(wordIdx * 4);
			if (selVal & (1 << bitIdx))
			{
				o.col.a = -1;				
			}
		}
	}
    return o;
}

bool _UseHashedAlphaTest;
Texture2DArray _HAT_BlueNoise;

half4 frag (v2f i) : SV_Target
{
	float power = -dot(i.pos, i.pos);
	half alpha = exp(power);
	if (i.col.a >= 0)
	{
		alpha = saturate(alpha * i.col.a);
	}
	else
	{
		// "selected" splat: magenta outline, increase opacity, magenta tint
		half3 selectedColor = half3(1,0,1);
		if (alpha > 7.0/255.0)
		{
			if (alpha < 10.0/255.0)
			{
				alpha = 1;
				i.col.rgb = selectedColor;
			}
			alpha = saturate(alpha + 0.3);
		}
		i.col.rgb = lerp(i.col.rgb, selectedColor, 0.5);
	}
	
    if (alpha < 1.0/255.0)
        discard;

	if (!_UseHashedAlphaTest)
		i.col.rgb *= alpha;
	else
	{
		float3 hatCoord;
		hatCoord.xy = i.vertex.xy; //@TODO better coord?
		hatCoord.z = i.idx;
		//alpha = InvSquareCentered01(alpha);
		//alpha = lerp(alpha, alpha*alpha, 0.7);
		//alpha = smoothstep(0,1, alpha);

		// "Hashed Alpha Testing", Wyman, McGuire 2017
		// https://casual-effects.com/research/Wyman2017Hashed/index.html
		// Instead of using 3D hash like in paper, this uses a 64x64x64 blue
		// noise texture from "Free blue noise textures", Peters 2016
		// https://momentsingraphics.de/BlueNoise.html
	    uint4 coord;
	    coord.xyz = (uint3)hatCoord;
	    coord.xyz &= 63;
	    coord.w = 0;
		half cutoff = _HAT_BlueNoise.Load(coord).r;
		clip(alpha - cutoff);
		alpha = 1;
	}
    half4 res = half4(i.col.rgb, alpha);
    return res;
}
ENDCG
        }
    }
}
