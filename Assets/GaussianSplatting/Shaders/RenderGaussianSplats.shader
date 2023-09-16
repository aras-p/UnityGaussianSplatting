Shader "Gaussian Splatting/Render Splats"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            ZWrite Off
            Blend OneMinusDstAlpha One
            Cull Off
            
CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc metal vulkan

#include "GaussianSplatting.hlsl"

StructuredBuffer<uint> _OrderBuffer;

struct v2f
{
    half4 col : COLOR0;
    float2 centerScreenPos : TEXCOORD3;
    float3 conic : TEXCOORD4;
    float4 vertex : SV_POSITION;
};

StructuredBuffer<SplatViewData> _SplatViewData;

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o = (v2f)0;
    instID = _OrderBuffer[instID];
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
		o.conic = view.conicRadius.xyz;

		o.centerScreenPos = (centerClipPos.xy / centerClipPos.w * float2(0.5, 0.5*_ProjectionParams.x) + 0.5) * _ScreenParams.xy;

		uint idx = vtxID;
		float2 quadPos = float2(idx&1, (idx>>1)&1) * 2.0 - 1.0;

		float radius = view.conicRadius.w;

		float2 deltaScreenPos = quadPos * radius * 2 / _ScreenParams.xy;
		o.vertex = centerClipPos;
		o.vertex.xy += deltaScreenPos * centerClipPos.w;
	}
    return o;
}

half4 frag (v2f i) : SV_Target
{
    float2 d = CalcScreenSpaceDelta(i.vertex.xy, i.centerScreenPos, _ProjectionParams);
    float power = CalcPowerFromConic(i.conic, d);
    i.col.a *= saturate(exp(power));
    if (i.col.a < 1.0/255.0)
        discard;

    half4 res = half4(i.col.rgb * i.col.a, i.col.a);
    return res;
}
ENDCG
        }
    }
}
