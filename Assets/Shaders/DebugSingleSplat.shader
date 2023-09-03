Shader "Gaussian Splatting/Debug Single Splat"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent-1" }
        LOD 100

        Pass
        {
            ZWrite Off
            Blend OneMinusDstAlpha One
CGPROGRAM
#pragma vertex vert
#pragma fragment frag

#include "UnityCG.cginc"
#include "GaussianSplatting.hlsl"

struct appdata
{
    float4 vertex : POSITION;
};

struct v2f
{
    float4 vertex : SV_POSITION;
    float2 centerScreenPos : TEXCOORD0;
    float3 conic : TEXCOORD1;
};

v2f vert (appdata v)
{
    v2f o;
    float3 centerWorldPos = unity_ObjectToWorld._m03_m13_m23;
    float4 centerClipPos = mul(UNITY_MATRIX_VP, float4(centerWorldPos, 1));
    o.centerScreenPos = (centerClipPos.xy / centerClipPos.w * float2(0.5, 0.5*_ProjectionParams.x) + 0.5) * _ScreenParams.xy;

    // covariance 3D
    float3x3 rotMat = (float3x3)unity_ObjectToWorld;
    rotMat /= 3;
    float3 cov3d0, cov3d1;
    CalcCovariance3D(rotMat, cov3d0, cov3d1);

    // covariance 2D
    float3 cov2d = CalcCovariance2D(centerWorldPos, cov3d0, cov3d1);

    // conic
    float3 conic = CalcConic(cov2d);

    v.vertex.xyz *= 2;
    o.vertex = UnityObjectToClipPos(v.vertex);
    o.conic = conic;
    return o;
}

half4 frag (v2f i) : SV_Target
{
    float2 d = CalcScreenSpaceDelta(i.vertex.xy, i.centerScreenPos);
    float power = CalcPowerFromConic(i.conic, d);

    half alpha = saturate(exp(power));
    half4 res = 1;
    res.rgb = 1 * alpha;
    res.a = alpha;
    return res;
}
ENDCG
        }
    }
}
