
#ifndef CALMWATER_REFRACTION_INCLUDED
#define CALMWATER_REFRACTION_INCLUDED


void ComputeRefraction(inout GlobalData data, v2f IN)
{
	float2 offset = data.worldNormal.xz * _GrabTexture_TexelSize.xy * _Distortion;

	// Depth Distortion ===================================================
	data.DepthUV = OffsetDepth(IN.DepthUV, offset);
	// GrabPass Distortion ================================================
	data.GrabUV = OffsetUV(IN.GrabUV, offset);

	// Refraction ============================================================
	// RGB 	= Color
	// A 	= Depth
	// =======================================================================
	float4 refraction = UNITY_SAMPLE_SCREENSPACE_TEXTURE(_GrabTexture, data.GrabUV.xy / data.GrabUV.w);
	float4 cleanRefraction = UNITY_SAMPLE_SCREENSPACE_TEXTURE(_GrabTexture, IN.GrabUV.xy / IN.GrabUV.w);

	//Depth Texture Clean
	data.cleanDepth = texDepth(IN.DepthUV);
	//Depth Texture Distorted
	data.depth = texDepth(data.DepthUV);

	//Depth	
	refraction.a = DistanceFade(data.depth, data.DepthUV.z, _DepthStart, _DepthEnd);
	//Clean Depth
	cleanRefraction.a = DistanceFade(data.cleanDepth, IN.DepthUV.z, _DepthStart, _DepthEnd);

	//TODO: Remove keyword in cull front pass and remove check CULL_FRONT here
	#ifndef CULL_FRONT
		#if _DISTORTIONQUALITY_HIGH 
		//Hide refraction from objects over the surface		
		refraction = data.DepthUV.z > data.depth ? cleanRefraction : refraction;
		#endif
	#endif

	//Final color with depth and refraction
	#ifndef CULL_FRONT
	#if _DEPTHFOG_ON
		float3 finalColor = lerp(_Color.rgb * refraction.rgb, _DepthColor.rgb * _LightColor0.rgb, 1.0 - refraction.a);
	#else
		float3 finalColor = lerp(_Color.rgb, _DepthColor.rgb, 1.0 - refraction.a) * refraction.rgb;
	#endif
	#else
		float3 finalColor = lerp(_Color.rgb, _DepthColor.rgb, 0.5) * refraction.rgb;
	#endif


	data.refractedBuffers = refraction;
	data.cleanBuffers = cleanRefraction;
	data.finalColor = finalColor;
}

#endif