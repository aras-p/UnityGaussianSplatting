#ifndef CALMWATER_FOAM_INCLUDED
#define CALMWATER_FOAM_INCLUDED


void ComputeFoam(inout GlobalData data, v2f IN) 
{

	#if _FOAM_ON || _WHITECAPS_ON

	#ifndef SHADER_API_D3D9
		float2 foamUV = IN.FoamUV.xy;
	#else
		float2 foamUV = IN.worldPos.xz;
	#endif

	fixed foamMask = 0;
	//Foam Texture with animation
	fixed3 foamTex = tex2D(_FoamTex, foamUV + (data.tangentNormal.xy * 0.05)).r;

	// ===========================================================================
	// FOAM
	// ===========================================================================
	#if _FOAM_ON
		//Border Foam Mask 
		foamMask = 1.0 - saturate(_FoamSize * (data.cleanDepth - data.DepthUV.z));
	#endif

	// ===========================================================================
	// WHITE CAPS
	// ===========================================================================
	#if _WHITECAPS_ON
	#ifndef SHADER_API_D3D9
		float2 maskUV = IN.FoamUV.zw;
	#else
		float2 maskUV = IN.worldPos.xz;
	#endif

		fixed capsMask = tex2D(_CapsMask, maskUV);

	#if _DISPLACEMENTMODE_WAVE || _DISPLACEMENTMODE_GERSTNER || _DISPLACEMENTMODE_TEXTURE
		capsMask *= IN.ambient.a;
	#endif

		capsMask = smoothstep(0, _CapsSize, capsMask);
		foamMask = max(_CapsIntensity * capsMask, foamMask);
	#endif

		foamTex *= foamMask.xxx * _FoamColor.rgb * max(_LightColor0.rgb, IN.ambient.rgb);
		data.finalColor += min(1.0, 2.0 * foamTex);

	#endif

}

#endif