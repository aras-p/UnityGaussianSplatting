#ifndef CALMWATER_REFLECTIONS_INCLUDED
#define CALMWATER_REFLECTIONS_INCLUDED

// =====================================================================
// Reflections 
// No Reflection on backface
// =====================================================================

void ComputeReflections(inout GlobalData data, v2f IN) 
{
	#ifndef CULL_FRONT

		//Reverse cubeMap Y to look like reflection
	#if _REFLECTIONTYPE_MIXED || _REFLECTIONTYPE_CUBEMAP
		half3 worldRefl = reflect(-data.worldViewDir, half3(data.worldNormal.x * _CubeDist, 1, data.worldNormal.z * _CubeDist));
		half3 cubeMap = texCUBE(_Cube, worldRefl).rgb * _CubeColor.rgb;
	#endif

	#if _REFLECTIONTYPE_MIXED || _REFLECTIONTYPE_REALTIME
		//Real Time reflections

		//TODO: Upgrade to GrabUV when unity fixes its bug
		fixed3 rtReflections = tex2Dproj(_ReflectionTex, UNITY_PROJ_COORD(data.DepthUV)) * _Reflection;
	#endif

	#if _REFLECTIONTYPE_MIXED
		fixed3 finalReflection = lerp(cubeMap, rtReflections, 0.5);
	#endif

	#if _REFLECTIONTYPE_REALTIME
		fixed3 finalReflection = rtReflections;
	#endif

	#if _REFLECTIONTYPE_CUBEMAP
		fixed3 finalReflection = cubeMap;
	#endif
		//end CULL_FRONT
	#endif 

	// ===========================================================================
	// Apply Reflections
	// ===========================================================================
	#ifndef CULL_FRONT
		float3 vertexNormals = float3(IN.tspace0.z, IN.tspace1.z, IN.tspace2.z);
		float3 reflectionNormals = lerp(vertexNormals, data.worldNormal, _ReflectionNormals);

		data.NdotV = NdotVTerm(reflectionNormals, data.worldViewDir);


	#if _REFLECTIONTYPE_MIXED || _REFLECTIONTYPE_REALTIME || _REFLECTIONTYPE_CUBEMAP

		// TEST: Use vertex normal for reflection fresnel
		//float NdotVertex = NdotVTerm(vertexNormals, worldViewDir);

		half fresnel = smoothstep(1 - saturate(data.NdotV), 0, _RimPower);
		data.finalColor = lerp(data.finalColor, finalReflection, fresnel * _Reflection);
	#endif
	#endif

}

#endif