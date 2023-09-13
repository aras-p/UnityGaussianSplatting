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

#include "UnityCG.cginc"
#include "GaussianSplatting.hlsl"

StructuredBuffer<uint> _OrderBuffer;

struct v2f
{
    half4 col : COLOR0;
    float2 centerScreenPos : TEXCOORD3;
    float3 conic : TEXCOORD4;
    float4 vertex : SV_POSITION;
};

float _SplatScale;
uint _SHOrder;

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o;
    instID = _OrderBuffer[instID];
	SplatData splat = LoadSplatData(instID);

    float4 boxRot = splat.rot;
    float3 boxSize = splat.scale;
    boxSize *= _SplatScale;

    float3x3 splatRotScaleMat = CalcMatrixFromRotationScale(boxRot, boxSize);

    float3 centerWorldPos = splat.pos * float3(1,1,-1);

    float3 viewDir = normalize(UnityWorldSpaceViewDir(centerWorldPos));

    o.col.rgb = ShadeSH(splat.sh, viewDir, _SHOrder);
    o.col.a = splat.opacity;

    float4 centerClipPos = mul(UNITY_MATRIX_VP, float4(centerWorldPos, 1));
	bool behindCam = centerClipPos.w <= 0;
    o.centerScreenPos = (centerClipPos.xy / centerClipPos.w * float2(0.5, 0.5*_ProjectionParams.x) + 0.5) * _ScreenParams.xy;

    float3 cov3d0, cov3d1;
    splatRotScaleMat[2] *= -1;
    CalcCovariance3D(splatRotScaleMat, cov3d0, cov3d1);
    float3 cov2d = CalcCovariance2D(centerWorldPos, cov3d0, cov3d1, UNITY_MATRIX_V, UNITY_MATRIX_P, _ScreenParams);

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
