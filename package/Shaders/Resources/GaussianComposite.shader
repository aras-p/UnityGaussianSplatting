// SPDX-License-Identifier: MIT
Shader "Hidden/Gaussian Splatting/Composite"
{
CGINCLUDE
#pragma require compute
#pragma use_dxc
#include "UnityCG.cginc"

struct v2f
{
    float4 vertex : SV_POSITION;
};

v2f vert (uint vtxID : SV_VertexID)
{
    v2f o;
    float2 quadPos = float2(vtxID&1, (vtxID>>1)&1) * 4.0 - 1.0;
	o.vertex = float4(quadPos, 1, 1);
    return o;
}

Texture2D _GaussianSplatRT;

half4 frag (v2f i) : SV_Target
{
    half4 col = _GaussianSplatRT.Load(int3(i.vertex.xy, 0));
    col.rgb = GammaToLinearSpace(col.a != 0 ? col.rgb/col.a : col.rgb);
    return float4(col.rgb, col.a);
}
ENDCG
    SubShader
    {
        ZWrite Off
        ZTest Always
        Cull Off

        // Composite splats
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
CGPROGRAM
#pragma vertex vert
#pragma fragment frag
ENDCG
        }

        // Stochastic temporal: average premultiplied splat color and coverage
        Pass
        {
            Blend Off
CGPROGRAM
#pragma vertex vert
#pragma fragment fragAccumulate
Texture2D _SplatHistory;
float _SplatFrameWeight;

half4 fragAccumulate(v2f i) : SV_Target
{
    half4 current = _GaussianSplatRT.Load(int3(i.vertex.xy, 0));
    if (_SplatFrameWeight == 1.0)
        return current;
    half4 history = _SplatHistory.Load(int3(i.vertex.xy, 0));
    return lerp(history, current, _SplatFrameWeight);
}
ENDCG
        }
    }
}
