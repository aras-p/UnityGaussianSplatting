Shader "Gaussian Splatting/Debug/Render Points"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            ZWrite On
            Cull Off
            
CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute

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
    half3 color : TEXCOORD0;
    float4 vertex : SV_POSITION;
};

float _SplatSize;
bool _DisplayIndex;
bool _DisplayLine;

static const float SH_C0 = 0.2820948;

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o;
    uint splatIndex;
    if (_DisplayLine)
        splatIndex = vtxID;
    else
        splatIndex = instID;

    float3 centerWorldPos = _DataBuffer[splatIndex].pos * float3(1,1,-1);

    float4 centerClipPos = mul(UNITY_MATRIX_VP, float4(centerWorldPos, 1));

    o.vertex = centerClipPos;
    if (!_DisplayLine)
    {
	    // two bits per vertex index to result in 0,1,2,1,3,2 from lowest: 0b1011'0110'0100
	    uint quadIndices = 0xB64;
	    uint idx = quadIndices >> (vtxID * 2);
        float2 quadPos = float2(idx&1, (idx>>1)&1) * 2.0 - 1.0;
        o.vertex.xy += (quadPos * _SplatSize / _ScreenParams.xy) * o.vertex.w;
    }

    o.color.rgb = saturate(SH_C0 * _DataBuffer[splatIndex].sh0 + 0.5);
    if (_DisplayIndex)
    {
        o.color.r = (splatIndex & 0xFFFF) / (float)0xFFFF;
        o.color.g = (splatIndex & 0xFFFFF) / (float)0xFFFFF;
        o.color.b = (splatIndex & 0xFFFFFF) / (float)0xFFFFFF;
    }
    return o;
}

half4 frag (v2f i) : SV_Target
{
    return half4(i.color.rgb, 1);
}
ENDCG
        }
    }
}
