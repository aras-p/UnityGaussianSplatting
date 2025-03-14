#include "CalmWater_Variables.cginc"
#include "CalmWater_Helper.cginc"

#include "CalmWater_Vertex.cginc"
#include "CalmWater_Tessellation.cginc"

#include "CalmWater_Normals.cginc"
#include "CalmWater_Refraction.cginc"
#include "CalmWater_Caustics.cginc"
#include "CalmWater_Scattering.cginc"
#include "CalmWater_Reflections.cginc"
#include "CalmWater_Foam.cginc"
#include "CalmWater_Specular.cginc"

#ifndef CALMWATER_INCLUDED
#define CALMWATER_INCLUDED


//Uncomment to enable enviro support
//#include "../../Enviro - Dynamic Enviroment/Resources/Shaders/Core/EnviroFogCore.cginc"

#ifndef LIGHTCOLOR
#define LIGHTCOLOR
uniform fixed4 _LightColor0;
#endif


// ============================================
// Frag
// ============================================
fixed4 frag( v2f i ) : SV_Target
{
	UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
	GlobalData data;
	InitializeGlobalData(data, i);

	ComputeNormals(data, i);
	ComputeRefraction(data, i);
	ComputeCaustics(data);
	ComputeScattering(data, i);
	ComputeReflections(data, i);
	ComputeFoam(data, i);
	ComputeSpecular(data);
	

	#ifdef UNITY_PASS_FORWARDADD
	data.finalColor *= _LightColor0.rgb;
	#endif

	data.finalColor += data.specular;

	//Alpha
	fixed alpha	= _EdgeFade * (data.cleanDepth - data.DepthUV.z) * _Color.a;

	fixed4 c;

	#ifndef UNITY_PASS_FORWARDADD
		//Uncomment to enable enviro support
		//half2 screenUV = (i.pos.xy / i.pos.w) * _ProjectionParams.x * 0.5 + 0.5;
		//diff = TransparentFog(float4(diff, 0), i.worldPos, screenUV, i.DepthUV.z).rgb;

		c.rgb 	= lerp(data.cleanBuffers.rgb, data.finalColor, saturate(alpha) );
		UNITY_APPLY_FOG(i.fogCoord, c);
	#else
		UNITY_LIGHT_ATTENUATION(atten, i, i.worldPos.xyz)
    	c.rgb 	= data.finalColor * saturate(alpha) * atten;
	#endif
	
	c.a 	= 1;

	return c;
}

#endif