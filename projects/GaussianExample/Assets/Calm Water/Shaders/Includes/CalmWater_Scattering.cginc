#ifndef CALMWATER_SCATTERING_INCLUDED
#define CALMWATER_SCATTERING_INCLUDED


void ComputeScattering(inout GlobalData data, v2f IN) 
{
	#ifdef UNITY_PASS_FORWARDBASE		
		#if _SCATTER_ON
			half sunScatter = max(0.0, dot(data.lightDir, -data.worldViewDir)) * _ScatterParams.x;
			half waveTips = smootherstep(IN.ambient.a * _ScatterParams.y);
			float scatterMask = pow(saturate(sunScatter) + saturate(waveTips), _ScatterParams.z);

			data.finalColor += _ScatterColor * saturate(scatterMask) * _LightColor0.rgb;
		#endif
	#endif

}

#endif