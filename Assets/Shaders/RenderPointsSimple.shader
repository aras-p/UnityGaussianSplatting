Shader "Unlit/RenderPointsSimple"
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

#include "UnityCG.cginc"

float3 QuatRotateVector(float3 v, float4 r)
{
    float3 t = 2 * cross(r.xyz, v);
    return v + r.w * t + cross(r.xyz, t);
}

float4 QuatInverse(float4 q)
{
    return rcp(dot(q, q)) * q * float4(-1,-1,-1,1);
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
StructuredBuffer<uint> _OrderBuffer;

struct v2f
{
    half4 col : COLOR0;
    float3 splatSize : TEXCOORD0;
    float3 splatSpaceCam : TEXCOORD1;
    float3 splatLocalPos : TEXCOORD2;
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

static const float SH_C0 = 0.2820948;
static const float SH_C1 = 0.4886025;
static const float SH_C2[] = {
    +1.0925484,
	-1.0925484,
    +0.3153916,
	-1.0925484,
	+0.5462742
};
static const float SH_C3[] = {
	-0.5900436,
	+2.8906114,
	-0.4570458,
	+0.3731763,
	-0.4570458,
	+1.4453057,
	-0.5900436
};

half3 ShadeSH(InputSplat splat, float3 dir)
{
    // ambient band
    half3 res = SH_C0 * splat.dc0;
    // 1st degree
    res = res - splat.sh0 * (dir.y * SH_C1) + splat.sh1 * (dir.z * SH_C1) - splat.sh2 * (dir.x * SH_C1);
    // 2nd degree
    res = res +
        (SH_C2[0] * dir.x * dir.y) * splat.sh4 +
		(SH_C2[1] * dir.y * dir.z) * splat.sh5 +
		(SH_C2[2] * (2.0f * dir.z * dir.z - dir.x * dir.x - dir.y * dir.y)) * splat.sh6 +
		(SH_C2[3] * dir.x * dir.z) * splat.sh7 +
		(SH_C2[4] * (dir.x * dir.x - dir.y * dir.y)) * splat.sh8;    
    //@TODO others
    return saturate(res + 0.5);
}

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o;
    instID = _OrderBuffer[instID];
    InputSplat splat = _DataBuffer[instID];

    int boxIdx = kCubeIndices[vtxID];
    float3 boxLocalPos = float3(boxIdx&1, (boxIdx>>1)&1, (boxIdx>>2)&1) * 2.0 - 1.0;
    float4 boxRot = normalize(splat.rot.yzwx); //@TODO: move normalize and swizzle offline
    float3 boxSize = exp(splat.scale) * 2.0; //@TODO: move exp offline

    boxLocalPos *= boxSize;
    float3 boxPos = QuatRotateVector(boxLocalPos, boxRot);
    float3 worldPos = splat.pos + boxPos;

    float3 viewDir = normalize(UnityWorldSpaceViewDir(worldPos));

    o.splatSize = boxSize;
    float4 invBoxRot = QuatInverse(boxRot);
    o.splatSpaceCam = QuatRotateVector(_WorldSpaceCameraPos.xyz - worldPos, invBoxRot);
    o.splatLocalPos = boxLocalPos;
    
    o.vertex = UnityObjectToClipPos(worldPos);
    o.col.rgb = ShadeSH(splat, viewDir);
    o.col.a = Sigmoid(splat.opacity); //@TODO: move offline
    o.psize = 10;
    return o;
}

// ray-ellipsoid intersection from https://iquilezles.org/articles/intersectors
float2 eliIntersect(float3 rayOrigin, float3 rayDir, float3 eliRadius)
{
    float3 ocn = rayOrigin/eliRadius;
    float3 rdn = rayDir/eliRadius;
    float a = dot(rdn, rdn);
    float b = dot(ocn, rdn);
    float c = dot(ocn, ocn);
    float h = b*b - a*(c-1.0);
    if (h < 0.0) return -1; //no intersection
    h = sqrt(h);
    return float2(-b-h,-b+h)/a;
}


half4 frag (v2f i) : SV_Target
{
    float3 rayOrigin = i.splatSpaceCam;
    float3 rayDir = normalize(i.splatLocalPos - i.splatSpaceCam);
    float2 isect = eliIntersect(rayOrigin, rayDir, i.splatSize);
    if (any(isect < 0.0))
    {
        i.col.a = 0;
    }
    else
    {
        float3 pos1 = rayOrigin + rayDir * isect.x;
        float3 pos2 = rayOrigin + rayDir * isect.y;
        float3 invSize2 = 1.0 / (i.splatSize * i.splatSize);
        float3 normal1 = normalize(pos1 * invSize2);
        float3 normal2 = normalize(pos2 * invSize2);
        float hackAlpha1 = saturate(1-(dot(normal1,normal2)*0.5+0.5));
        hackAlpha1 = pow(hackAlpha1, 3);
        i.col.a *= hackAlpha1;
    }

    return half4(i.col.rgb * i.col.a, i.col.a);
}
ENDCG
        }
    }
}
