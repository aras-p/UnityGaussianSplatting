Shader "Hidden/DrawSplatDataRanges"
{
    SubShader
    {
        Pass
        {
            ZWrite Off
            Cull Off
            Blend SrcAlpha One
CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute

#include "UnityCG.cginc"

ByteAddressBuffer _InputData;
float4 _Params;
float _DataMin[100];
float _DataMax[100];

struct v2f
{
    float4 pos : SV_POSITION;
    float psize : PSIZE;
};

v2f vert (uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    v2f o;
    float y = iid;

    uint addr = (vid * (uint)_Params.w + iid) * 4;
    float val = asfloat(_InputData.Load(addr));

    o.pos.x = (val - _DataMin[iid]) / (_DataMax[iid] - _DataMin[iid]) * _Params.x;
    o.pos.y = (y + lerp(0.1, 0.9, (vid&31)/31.0)) * _Params.z;
    o.pos.z = 0.5;
    o.pos.w = 1;
    o.pos = UnityObjectToClipPos(o.pos);
    o.psize = 1;
    return o;
}

half4 frag (v2f i) : SV_Target
{
    return half4(0.9, 0.5, 0.1, 0.3);
}
ENDCG
        }
    }
}
