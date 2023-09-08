Shader "Gaussian Splatting/Debug/Render Points"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            ZWrite Off
            Blend SrcAlpha One
            Cull Off
            
CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute

StructuredBuffer<float3> _InputPositions;
StructuredBuffer<uint> _OrderBuffer;

struct v2f
{
    float4 vertex : SV_POSITION;
};

float _SplatSize;

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o;
    instID = _OrderBuffer[instID];

    float3 centerWorldPos = _InputPositions[instID] * float3(1,1,-1);

    float4 centerClipPos = mul(UNITY_MATRIX_VP, float4(centerWorldPos, 1));
	bool behindCam = centerClipPos.w <= 0;
	centerClipPos /= centerClipPos.w;

	// two bits per vertex index to result in 0,1,2,1,3,2 from lowest: 0b1011'0110'0100
	uint quadIndices = 0xB64;
	uint idx = quadIndices >> (vtxID * 2);
    float2 quadPos = float2(idx&1, (idx>>1)&1) * 2.0 - 1.0;

	float2 deltaScreenPos = quadPos * _SplatSize / _ScreenParams.xy;
	o.vertex = float4(centerClipPos.xy + deltaScreenPos, 1, 1);
	if (behindCam)
		o.vertex = 0;
    return o;
}

half4 _Color;

half4 frag (v2f i) : SV_Target
{
    return _Color;
}
ENDCG
        }
    }
}
