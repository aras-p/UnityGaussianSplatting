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

//@TODO: cube face flip opts from https://twitter.com/SebAaltonen/status/1315985267258519553
static const int kCubeIndices[36] =
{
    0, 1, 2, 1, 3, 2,
    4, 6, 5, 5, 6, 7,
    0, 2, 4, 4, 2, 6,
    1, 5, 3, 5, 7, 3,
    0, 4, 1, 4, 5, 1,
    2, 3, 6, 3, 7, 6
};

#include "UnityCG.cginc"
#include "GaussianSplatting.hlsl"

StructuredBuffer<uint> _OrderBuffer;

/*
struct ChunkData
{
    float3 bmin;
    float3 bmax;
};
StructuredBuffer<ChunkData> _ChunkBuffer;
bool _DisplayChunks;
int _ChunkCount;
*/

struct v2f
{
    half4 col : COLOR0;
    float4 vertex : SV_POSITION;
};

float _SplatScale;

// based on https://iquilezles.org/articles/palettes/
// cosine based palette, 4 vec3 params
half3 palette(float t, half3 a, half3 b, half3 c, half3 d)
{
    return a + b*cos(6.28318*(c*t+d));
}

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o;
    bool chunks = false; //@TODO _DisplayChunks;
	uint idx = kCubeIndices[vtxID];
	float3 localPos = float3(idx&1, (idx>>1)&1, (idx>>2)&1) * 2.0 - 1.0;

    float3 centerWorldPos = 0;

    if (!chunks)
    {
        // display splat boxes
        instID = _OrderBuffer[instID];
        SplatData splat = LoadSplatData(instID);

        float4 boxRot = splat.rot;
        float3 boxSize = splat.scale;
        boxSize *= _SplatScale;

        float3x3 splatRotScaleMat = CalcMatrixFromRotationScale(boxRot, boxSize);

        centerWorldPos = splat.pos * float3(1,1,-1);

        o.col.rgb = saturate(splat.sh.col);
        o.col.a = saturate(splat.opacity);

        localPos = mul(splatRotScaleMat, localPos) * 2;
    }
    else
    {
        // display chunk boxes @TODO
        /*
        localPos = localPos * 0.5 + 0.5;
        ChunkData chunk = _ChunkBuffer[instID];
        localPos = lerp(chunk.bmin, chunk.bmax, localPos);

        o.col.rgb = palette((float)instID / (float)_ChunkCount, half3(0.5,0.5,0.5), half3(0.5,0.5,0.5), half3(1,1,1), half3(0.0, 0.33, 0.67));
        o.col.a = 0.1;
        */
    }
    localPos.z *= -1;

    float3 worldPos = centerWorldPos + localPos;
    o.vertex = UnityWorldToClipPos(worldPos);

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
