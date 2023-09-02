Shader "Gaussian Splatting/Render Splats"
{
    Properties
    {
        _SplatScale("Splat Scale", Range(0.1,3.0)) = 1.0
    }
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
    float2 centerScreenPos : TEXCOORD3;
    float3 conic : TEXCOORD4;
    float4 vertex : SV_POSITION;
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
    dir = -dir;

    // ambient band
    half3 res = SH_C0 * splat.dc0;
    // 1st degree
    res = res - splat.sh0 * (dir.y * SH_C1) + splat.sh1 * (dir.z * SH_C1) - splat.sh2 * (dir.x * SH_C1);
    // 2nd degree
    res = res +
        (SH_C2[0] * dir.x * dir.y) * splat.sh4 +
		(SH_C2[1] * dir.y * dir.z) * splat.sh5 +
		(SH_C2[2] * (2 * dir.z * dir.z - dir.x * dir.x - dir.y * dir.y)) * splat.sh6 +
		(SH_C2[3] * dir.x * dir.z) * splat.sh7 +
		(SH_C2[4] * (dir.x * dir.x - dir.y * dir.y)) * splat.sh8;
    //@TODO 3rd degree
    return saturate(res + 0.5);
}

float3x3 CalcMatrixFromRotationScale(float4 rot, float3 scale)
{
    float3x3 ms = float3x3(
        scale.x, 0, 0,
        0, scale.y, 0,
        0, 0, scale.z
    );
    float x = rot.x;
    float y = rot.y;
    float z = rot.z;
    float w = rot.w;
    float3x3 mr = float3x3(
        1-2*(y*y + z*z),   2*(x*y - w*z),   2*(x*z + w*y),
          2*(x*y + w*z), 1-2*(x*x + z*z),   2*(y*z - w*x),
          2*(x*z - w*y),   2*(y*z + w*x), 1-2*(x*x + y*y)
    );
    return mul(mr, ms);
}

void CalcCovariance3D(float3x3 rotMat, out float3 sigma0, out float3 sigma1)
{
    float3x3 sig = mul(rotMat, transpose(rotMat));
    sigma0 = float3(sig._m00, sig._m01, sig._m02);
    sigma1 = float3(sig._m11, sig._m12, sig._m22);
}

// from "EWA Splatting" (Zwicker et al 2002) eq. 31
float3 CalcCovariance2D(float3 worldPos, float3 cov3d0, float3 cov3d1)
{
    float4x4 viewMatrix = UNITY_MATRIX_V;
    float3 viewPos = mul(viewMatrix, float4(worldPos, 1)).xyz;

    float focal = _ScreenParams.x;

    float4x4 J = float4x4(
        focal / viewPos.z, 0, -(focal * viewPos.x) / (viewPos.z * viewPos.z), 0,
        0, focal / viewPos.z, -(focal * viewPos.y) / (viewPos.z * viewPos.z), 0,
        0, 0, 0, 0,
        0, 0, 0, 0
    );
    viewMatrix._m03_m13_m23 = 0;
    float4x4 W = viewMatrix;
    float4x4 T = mul(J, W);
    float4x4 V = float4x4(
        cov3d0.x, cov3d0.y, cov3d0.z, 0,
        cov3d0.y, cov3d1.x, cov3d1.y, 0,
        cov3d0.z, cov3d1.y, cov3d1.z, 0,
        0, 0, 0, 0
    );
    float4x4 cov = mul(T, mul(V, transpose(T)));

    // Low pass filter to make each splat at least 1px size.
    cov._m00 += 0.3;
    cov._m11 += 0.3;
    return float3(cov._m00, cov._m01, cov._m11);
}

float3 CalcConic(float3 cov2d)
{
    float det = cov2d.x * cov2d.z - cov2d.y * cov2d.y;
    return float3(cov2d.z, -cov2d.y, cov2d.x) * rcp(det);
}

float _SplatScale;

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o;
    instID = _OrderBuffer[instID];
    InputSplat splat = _DataBuffer[instID];

    int boxIdx = kCubeIndices[vtxID];
    float3 boxLocalPos = float3(boxIdx&1, (boxIdx>>1)&1, (boxIdx>>2)&1) * 2.0 - 1.0;
    float4 boxRot = normalize(splat.rot.yzwx); //@TODO: move normalize and swizzle offline
    float3 boxSize = exp(splat.scale); //@TODO: move exp offline
    boxSize *= _SplatScale;

    float3x3 splatRotScaleMat = CalcMatrixFromRotationScale(boxRot, boxSize);

    #if 0
    boxLocalPos *= boxSize * 4;
    float3 boxPos = QuatRotateVector(boxLocalPos, boxRot);
    #else
    float3 boxPos = mul(splatRotScaleMat, boxLocalPos) * 4;
    #endif
    boxPos.z *= -1;

    float3 centerWorldPos = splat.pos * float3(1,1,-1);
    float3 worldPos = centerWorldPos + boxPos;

    float3 viewDir = normalize(UnityWorldSpaceViewDir(worldPos));

    o.vertex = UnityObjectToClipPos(worldPos);
    o.col.rgb = ShadeSH(splat, viewDir);
    o.col.a = Sigmoid(splat.opacity); //@TODO: move offline

    float4 centerClipPos = mul(UNITY_MATRIX_VP, float4(centerWorldPos, 1));
    o.centerScreenPos = (centerClipPos.xy / centerClipPos.w * float2(0.5, 0.5*_ProjectionParams.x) + 0.5) * _ScreenParams.xy;

    float3 cov3d0, cov3d1;
    splatRotScaleMat[2] *= -1;
    CalcCovariance3D(splatRotScaleMat, cov3d0, cov3d1);
    float3 cov2d = CalcCovariance2D(centerWorldPos, cov3d0, cov3d1);
    o.conic = CalcConic(cov2d);

    return o;
}

half4 frag (v2f i) : SV_Target
{
    float2 d = i.vertex.xy - i.centerScreenPos;
    d.y *= _ProjectionParams.x;
    float pwr = -0.5 * (i.conic.x * d.x*d.x + i.conic.z * d.y*d.y) + i.conic.y * d.x*d.y;
    i.col.a *= saturate(exp(pwr));
    if (i.col.a < 1.0/255.0)
        discard;

    half4 res = half4(i.col.rgb * i.col.a, i.col.a);
    return res;
}
ENDCG
        }
    }
}
