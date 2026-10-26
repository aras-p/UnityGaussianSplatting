// SPDX-License-Identifier: MIT
Shader "Gaussian Splatting/Debug/Render Boxes"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            ZWrite Off
            Blend OneMinusDstAlpha One
            Cull Front

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc

#include "UnityCG.cginc"
#include "GaussianSplatting.hlsl"

StructuredBuffer<uint> _OrderBuffer;


struct v2f
{
    half4 col : COLOR0;
    float4 vertex : SV_POSITION;
};

float _SplatScale;
float _SplatOpacityScale;

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o;
	uint idx = vtxID;
	float3 localPos = float3(idx&1, (idx>>1)&1, (idx>>2)&1) * 2.0 - 1.0;

    float3 centerWorldPos = 0;

    // display splat boxes
    instID = _OrderBuffer[instID];
    SplatData splat = LoadSplatData(instID);

    float4 boxRot = splat.rot;
    float3 boxSize = splat.scale;
    boxSize *= _SplatScale;

    float3x3 splatRotScaleMat = CalcMatrixFromRotationScale(boxRot, boxSize);
    splatRotScaleMat = mul((float3x3)unity_ObjectToWorld, splatRotScaleMat);

    centerWorldPos = splat.pos;
    centerWorldPos = mul(unity_ObjectToWorld, float4(centerWorldPos,1)).xyz;

    o.col.rgb = saturate(splat.sh.col);
    o.col.a = saturate(splat.opacity * _SplatOpacityScale);

    localPos = mul(splatRotScaleMat, localPos) * 2;

    float3 worldPos = centerWorldPos + localPos;
    o.vertex = UnityWorldToClipPos(worldPos);
    FlipProjectionIfBackbuffer(o.vertex);
    return o;
}

half4 frag (v2f i) : SV_Target
{
    half4 res = half4(i.col.rgb * i.col.a, i.col.a);
    return res;
}
ENDCG
        }
    }
}
