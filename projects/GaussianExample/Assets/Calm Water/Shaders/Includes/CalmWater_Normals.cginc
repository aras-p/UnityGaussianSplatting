
#ifndef CALMWATER_NORMALS_INCLUDED
#define CALMWATER_NORMALS_INCLUDED

float3 NormalBlendReoriented(float3 A, float3 B)
{
	float3 t = A.xyz + float3(0.0, 0.0, 1.0);
	float3 u = B.xyz * float3(-1.0, -1.0, 1.0);
	return (t / t.z) * dot(t, u) - u;
}


void ComputeNormals(inout GlobalData data, v2f IN)
{
	// NormalMaps
	float3 tangentNormal = float3(0, 0, 1);

#if _BUMPMODE_SINGLE || _BUMPMODE_DUAL
	float3 n1 = UnpackNormalScale(tex2D(_BumpMap, IN.AnimUV.xy), _BumpStrength);
	float3 n2 = UnpackNormalScale(tex2D(_BumpMap, IN.AnimUV.zw), _BumpStrength);
	tangentNormal = NormalBlendReoriented(n1, n2);

	#if _BUMPMODE_DUAL
	float3 n3 = UnpackNormalScale(tex2D(_BumpMapLarge, IN.AnimUV2), _BumpLargeStrength);
	tangentNormal = NormalBlendReoriented(tangentNormal, n3);
	#endif

#endif


#if _BUMPMODE_FLOWMAP
	half4 bump = SampleFlowMap(_BumpMap, IN.BumpUV, _FlowMap, IN.FlowMapUV, _FlowSpeed, _FlowIntensity);
	tangentNormal = UnpackNormalScale(bump, _BumpStrength);
#endif

	// Tangent Normal
	data.tangentNormal = tangentNormal;
	// World Normal
	data.worldNormal = WorldNormal(IN.tspace0.xyz, IN.tspace1.xyz, IN.tspace2.xyz, tangentNormal);
}

#endif