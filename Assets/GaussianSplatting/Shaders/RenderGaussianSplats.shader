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

#include "UnityCG.cginc"
#include "GaussianSplatting.hlsl"

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
StructuredBuffer<uint> _OrderBuffer;

struct v2f
{
    half4 col : COLOR0;
    float2 centerScreenPos : TEXCOORD3;
    float3 conic : TEXCOORD4;
    float4 vertex : SV_POSITION;
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

int _SHOrder;

half3 ShadeSH(InputSplat splat, float3 dir)
{
	dir *= -1;
	dir.z *= -1;

    float x = dir.x, y = dir.y, z = dir.z;

    // ambient band
    half3 res = SH_C0 * splat.sh0;
    // 1st degree
	if (_SHOrder >= 1)
	{
		res += SH_C1 * (-splat.sh1 * y + splat.sh2 * z - splat.sh3 * x);
		// 2nd degree
		if (_SHOrder >= 2)
		{
			float xx = x * x, yy = y * y, zz = z * z;
			float xy = x * y, yz = y * z, xz = x * z;
			res +=
				(SH_C2[0] * xy) * splat.sh4 +
				(SH_C2[1] * yz) * splat.sh5 +
				(SH_C2[2] * (2 * zz - xx - yy)) * splat.sh6 +
				(SH_C2[3] * xz) * splat.sh7 +
				(SH_C2[4] * (xx - yy)) * splat.sh8;
			// 3rd degree
			if (_SHOrder >= 3)
			{
				res +=
					(SH_C3[0] * y * (3 * xx - yy)) * splat.sh9 +
					(SH_C3[1] * xy * z) * splat.sh10 +
					(SH_C3[2] * y * (4 * zz - xx - yy)) * splat.sh11 +
					(SH_C3[3] * z * (2 * zz - 3 * xx - 3 * yy)) * splat.sh12 +
					(SH_C3[4] * x * (4 * zz - xx - yy)) * splat.sh13 +
					(SH_C3[5] * z * (xx - yy)) * splat.sh14 +
					(SH_C3[6] * x * (xx - 3 * yy)) * splat.sh15;
			}
		}
	}
    return max(res + 0.5, 0);
}

float _SplatScale;

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o;
    instID = _OrderBuffer[instID];
    InputSplat splat = _DataBuffer[instID];

    float4 boxRot = normalize(splat.rot.yzwx); //@TODO: move normalize and swizzle offline
    float3 boxSize = exp(splat.scale); //@TODO: move exp offline
    boxSize *= _SplatScale;

    float3x3 splatRotScaleMat = CalcMatrixFromRotationScale(boxRot, boxSize);

    float3 centerWorldPos = splat.pos * float3(1,1,-1);

    float3 viewDir = normalize(UnityWorldSpaceViewDir(centerWorldPos));

    o.col.rgb = ShadeSH(splat, viewDir);
    o.col.a = Sigmoid(splat.opacity); //@TODO: move offline

    float4 centerClipPos = mul(UNITY_MATRIX_VP, float4(centerWorldPos, 1));
	bool behindCam = centerClipPos.w <= 0;
    o.centerScreenPos = (centerClipPos.xy / centerClipPos.w * float2(0.5, 0.5*_ProjectionParams.x) + 0.5) * _ScreenParams.xy;

    float3 cov3d0, cov3d1;
    splatRotScaleMat[2] *= -1;
    CalcCovariance3D(splatRotScaleMat, cov3d0, cov3d1);
    float3 cov2d = CalcCovariance2D(centerWorldPos, cov3d0, cov3d1);

	// conic
    float det = cov2d.x * cov2d.z - cov2d.y * cov2d.y;
	float3 conic = float3(cov2d.z, -cov2d.y, cov2d.x) * rcp(det);
	o.conic = conic;

	// make the quad in screenspace the required size to cover the extents
	// of the 2D splat.
	//@TODO: should be possible to orient the quad to cover an elongated
	// splat tighter

	// two bits per vertex index to result in 0,1,2,1,3,2 from lowest:
	// 0b1011'0110'0100
	uint quadIndices = 0xB64;
	uint idx = quadIndices >> (vtxID * 2);
    float2 quadPos = float2(idx&1, (idx>>1)&1) * 2.0 - 1.0;

	float mid = 0.5f * (cov2d.x + cov2d.z);
	float lambda1 = mid + sqrt(max(0.1f, mid * mid - det));
	float lambda2 = mid - sqrt(max(0.1f, mid * mid - det));
	float radius = ceil(3.f * sqrt(max(lambda1, lambda2)));

	float2 deltaScreenPos = quadPos * radius * 2 / _ScreenParams.xy;
	o.vertex = centerClipPos;
	o.vertex.xy += deltaScreenPos * centerClipPos.w;

	if (behindCam)
		o.vertex = 0.0 / 0.0;
    return o;
}

half4 frag (v2f i) : SV_Target
{
    float2 d = CalcScreenSpaceDelta(i.vertex.xy, i.centerScreenPos);
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
