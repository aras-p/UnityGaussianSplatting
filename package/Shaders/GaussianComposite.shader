// SPDX-License-Identifier: MIT
Shader "Hidden/Gaussian Splatting/Composite"
{
    SubShader
    {
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc
#pragma require 2darray
#pragma multi_compile_instancing
#pragma require instancing

// Enable proper multi-compile support for all stereo rendering modes
#pragma multi_compile_local _ UNITY_SINGLE_PASS_STEREO STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON

#include "UnityCG.cginc"

struct v2f
{
    float4 vertex : SV_POSITION;
    float2 uv : TEXCOORD0;
};

struct appdata
{
    float4 vertex : POSITION;
    float2 uv : TEXCOORD0;
    uint vtxID : SV_VertexID;
};

v2f vert (appdata v)
{
    v2f o;
    uint vtxID = v.vtxID;
    
    float2 quadPos = float2(vtxID&1, (vtxID>>1)&1) * 4.0 - 1.0;
    o.vertex = UnityObjectToClipPos(float4(quadPos, 1, 1));

    o.uv = float2(vtxID&1, (vtxID>>1)&1);
    return o;
}

// Separate textures for left and right eyes
#if defined(UNITY_SINGLE_PASS_STEREO) || defined(STEREO_INSTANCING_ON) || defined(STEREO_MULTIVIEW_ON)
UNITY_DECLARE_TEX2DARRAY(_GaussianSplatRT);
#else
Texture2D _GaussianSplatRT;
#endif

int _CustomStereoEyeIndex;
half4 frag (v2f i) : SV_Target
{
    uint eyeIndex = _CustomStereoEyeIndex;
    // return float4(eyeIndex == 0 ? 1 : 0, 0, eyeIndex == 1 ? 1 : 0, 1); // Red = left, Blue = right
    
    // Normalize the pixel coordinates to [0,1] range
    float2 normalizedUV = float2(i.vertex.x / _ScreenParams.x, i.vertex.y / _ScreenParams.y);
    
#if 1
    half4 col1, col2, col;
    
    // // Check if using separate eye textures
    #if defined(UNITY_SINGLE_PASS_STEREO) || defined(STEREO_INSTANCING_ON) || defined(STEREO_MULTIVIEW_ON)
        col1 = UNITY_SAMPLE_TEX2DARRAY(_GaussianSplatRT, float3(normalizedUV, 0));
        col2 = UNITY_SAMPLE_TEX2DARRAY(_GaussianSplatRT, float3(normalizedUV, 1));
        col = col2 * eyeIndex + col1 * (1 - eyeIndex);
    #else
        // Fallback to legacy single-texture approach for backward compatibility
        col = _GaussianSplatRT.Load(int3(i.vertex.xy, 0));
    #endif
#endif

    col.rgb = GammaToLinearSpace(col.rgb);
    col.a = saturate(col.a * 1.5);
    return col;
}
ENDCG
        }
    }
}
