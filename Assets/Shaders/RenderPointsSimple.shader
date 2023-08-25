Shader "Unlit/RenderPointsSimple"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" }

        Pass
        {
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Front
            
CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute

#include "UnityCG.cginc"

float4 QuatMul(float4 q1, float4 q2)
{
    return float4(
        q2.xyz * q1.w + q1.xyz * q2.w + cross(q1.xyz, q2.xyz),
        q1.w * q2.w - dot(q1.xyz, q2.xyz)
    );
}

float3 QuatRotateVector(float3 v, float4 r)
{
    float4 rc = r * float4(-1, -1, -1, 1);
    return QuatMul(r, QuatMul(float4(v, 0), rc)).xyz;
}

float3x3 QuatToMatrix(float4 q)
{
  float qx = q.y;
  float qy = q.z;
  float qz = q.w;
  float qw = q.x;

  float qxx = qx * qx;
  float qyy = qy * qy;
  float qzz = qz * qz;
  float qxz = qx * qz;
  float qxy = qx * qy;
  float qyw = qy * qw;
  float qzw = qz * qw;
  float qyz = qy * qz;
  float qxw = qx * qw;

  return float3x3(
    float3(1.0 - 2.0 * (qyy + qzz), 2.0 * (qxy - qzw), 2.0 * (qxz + qyw)),
    float3(2.0 * (qxy + qzw), 1.0 - 2.0 * (qxx + qzz), 2.0 * (qyz - qxw)),
    float3(2.0 * (qxz - qyw), 2.0 * (qyz + qxw), 1.0 - 2.0 * (qxx + qyy))
  );
}

float Sigmoid(float v)
{
	return rcp(1.0 + exp(-v));
}


struct InputSplat
{
    float3 pos;
    float3 nor;
    float3 dc0;
    float3 sh0, sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, sh10, sh11, sh12, sh13, sh14;
    float opacity;
    float3 scale;
    float4 rot;
};
StructuredBuffer<InputSplat> _DataBuffer;

struct v2f
{
    half4 col : COLOR0;
    float4 vertex : SV_POSITION;
    float psize : PSIZE;
};

static const int kCubeIndices[36] =
{
    //@TODO: cube face flip opts from https://twitter.com/SebAaltonen/status/1315985267258519553?lang=en
    0, 1, 2, 1, 3, 2,
    4, 6, 5, 5, 6, 7,
    0, 2, 4, 4, 2, 6,
    1, 5, 3, 5, 7, 3,
    0, 4, 1, 4, 5, 1,
    2, 3, 6, 3, 7, 6
};

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o;
    InputSplat splat = _DataBuffer[instID];

    int boxIdx = kCubeIndices[vtxID];
    float3 boxPos = float3(boxIdx&1, (boxIdx>>1)&1, (boxIdx>>2)&1) * 2.0 - 1.0;
    float4 boxRot = normalize(splat.rot); //@TODO: move normalize offline
    float3 boxSize = exp(splat.scale) * 2.0; //@TODO: move exp offline

    #if 0 //@TODO: why no work?
    boxPos = QuatRotateVector(boxPos * boxSize, boxRot);
    #else
    float3x3 mat = QuatToMatrix(boxRot);
    boxPos = mul(mat, boxPos * boxSize);
    #endif
    float3 worldPos = splat.pos + boxPos;
    
    o.vertex = UnityObjectToClipPos(worldPos);
    o.col.rgb = splat.dc0 * 0.2 + 0.5;
    o.col.a = Sigmoid(splat.opacity); //@TODO: move offline
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
