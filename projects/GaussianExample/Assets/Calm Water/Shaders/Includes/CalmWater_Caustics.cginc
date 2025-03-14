#ifndef CALMWATER_CAUSTICS_INCLUDED
#define CALMWATER_CAUSTICS_INCLUDED


// ===========================================================================
// Caustics
// ===========================================================================
void ComputeCaustics(inout GlobalData data) 
{
	#ifdef UNITY_PASS_FORWARDBASE
		#ifndef CULL_FRONT
			#if _CAUSTICS_ON
				float2 causticsUV = ProjectedWorldPos(data.worldPosition.xyz, data.depth, data.DepthUV.z).xz / _CausticsTex_ST.xy + _CausticsTex_ST.zw;
				causticsUV += data.worldNormal.xy * 0.0015 * _Distortion;
				causticsUV += frac(_Time.x * _CausticsSpeed);

				float causticsDepth = DistanceFade(data.depth, data.DepthUV.z, _CausticsStart, _CausticsEnd);

				data.finalColor += tex2D(_CausticsTex, causticsUV) * causticsDepth * data.finalColor * _CausticsIntensity * _LightColor0.rgb;
			#endif
		#endif
	#endif
}

#endif