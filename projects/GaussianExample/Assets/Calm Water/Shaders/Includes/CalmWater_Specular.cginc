#ifndef CALMWATER_SPECULAR_INCLUDED
#define CALMWATER_SPECULAR_INCLUDED


void ComputeSpecular(inout GlobalData data) 
{

#ifndef CULL_FRONT
	float waveFresnel = FresnelSpecular(saturate(data.NdotV), _specFresnel);
	data.finalColor += waveFresnel * _LightColor0.rgb * _SpecColor.rgb * UNITY_LIGHTMODEL_AMBIENT * _specIntensity;
#endif


	data.specular = SpecularColor(_Smoothness, data.lightDir, data.worldViewDir, data.worldNormal);
}

#endif