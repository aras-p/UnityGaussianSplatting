Shader "Unlit/RenderPointsSimple"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" }

        Pass
        {
            //ZWrite Off
            //Blend SrcAlpha OneMinusSrcAlpha
            
CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute

#include "UnityCG.cginc"

struct InputVertex
{
    float3 pos;
    float3 nor;
    float3 dc0;
    float3 sh0, sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, sh10, sh11, sh12, sh13, sh14;
    float opacity;
    float3 scale;
    float4 rot;
};
StructuredBuffer<InputVertex> _DataBuffer;

struct v2f
{
    half4 col : COLOR0;
    float4 vertex : SV_POSITION;
    float psize : PSIZE;
};

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o;
    InputVertex vtx = _DataBuffer[instID];
    o.vertex = UnityObjectToClipPos(vtx.pos);
    o.col.rgb = vtx.dc0;
    o.col.a = vtx.opacity / 255;
    o.psize = 10;
    return o;
}

half4 frag (v2f i) : SV_Target
{
    return i.col;
}
ENDCG
        }
    }
}
