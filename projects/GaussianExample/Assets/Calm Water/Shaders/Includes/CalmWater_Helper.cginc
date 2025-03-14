// Upgrade NOTE: replaced '_World2Object' with 'unity_WorldToObject'
#include "CalmWater_Variables.cginc"


#ifndef CALMWATER_HELPER_INCLUDED
#define CALMWATER_HELPER_INCLUDED


// =====================================
// Lighting
// =====================================

// NOTE: some intricacy in shader compiler on some GLES2.0 platforms (iOS) needs 'viewDir' & 'h'
// to be mediump instead of lowp, otherwise specular highlight becomes too bright.

// Lighting Terms ===============================================================================
half DiffuseTerm (half3 normalDir,half3 lightDir){
	return max (0, dot(normalDir,lightDir));
}

half NdotVTerm(half3 normalDir,half3 viewDir){
	return dot(normalDir,viewDir);
}

float3 SpecularColor (half gloss, half3 lightDir,half3 viewDir,half3 normalDir)
{
	half reflectiveFactor 	= max(0.0, dot(-viewDir, reflect(lightDir, normalDir)));
	//half diffuseFactor 		= max(0.0, dot(normalDir, lightDir));
	half3 spec			 	= pow(reflectiveFactor, gloss * 128);

	return _LightColor0.rgb * _SpecColor.rgb * spec;
}

float FresnelSpecular(float NdotV, float power)
{
	float fresnel = pow(max(0.0, 1.0 - NdotV), power);
	float result = fresnel * fresnel;

	return saturate(result);
}

// Helpers ======================================================================================

float smootherstep(float x) {
	x = saturate(x);
	return saturate(x * x * x * (x * (6 * x - 15) + 10));
}

inline float4 AnimateBump(float2 uv){

//	#if _WORLDSPACE_ON
//	uv = -uv;
//	#endif
//
	float4 coords;

	coords.xy = TRANSFORM_TEX(uv,_BumpMap);
	coords.zw = TRANSFORM_TEX(uv,_BumpMap) * 0.5;
	coords += frac(_Speeds * _Time.x);

	return coords;
}

inline float2 AnimateLargeBump(half4 ST, float2 uv, float2 speed)
{
	float2 coords;
	coords = uv * ST.xy + ST.zw;
	coords += frac(speed * _Time.x);

	return coords;
}

inline half3 SafeNormalize(half3 inVec)
{
	half dp3 = max(0.001f, dot(inVec, inVec));
	return inVec * rsqrt(dp3);
}

inline fixed4 SampleFlowMap(
	sampler2D tex, 
	float2 texUV, 
	sampler2D flowMap,	
	float2 uv,
	float speed,
	float intensity) 
{
	half4 flowVal = (tex2D(flowMap, uv) * 2 - 1) * intensity;

	float dif1 = frac(_Time.x * speed + 0.5);
	float dif2 = frac(_Time.x * speed);

	half lerpVal = abs((0.5 - dif1) / 0.5);

	half4 col1 = tex2D(tex, texUV - flowVal.xy * dif1);
	half4 col2 = tex2D(tex, texUV - flowVal.xy * dif2);

	return lerp(col1, col2, lerpVal);
}

inline float4 OffsetUV(float4 uv, float2 offset)
{	
	#ifdef UNITY_Z_0_FAR_FROM_CLIPSPACE
		uv.xy = offset * UNITY_Z_0_FAR_FROM_CLIPSPACE(uv.z) + uv.xy;
	#else
		uv.xy = offset * uv.z + uv.xy;
	#endif

	return uv;
}

inline float4 OffsetDepth(float4 uv, float2 offset)
{	
	uv.xy = offset * uv.z + uv.xy;
	return uv;
}

inline float texDepth (float4 uv)
{
	return LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_PROJ(_CameraDepthTexture, UNITY_PROJ_COORD(uv)));
}

inline half3 WorldNormal(half3 t0,half3 t1, half3 t2, half3 bump)
{
	return normalize( half3( dot(t0, bump) , dot(t1, bump) , dot(t2, bump) ) );
}

inline float3 ProjectedWorldPos(float3 worldPos, float sceneDepth, float pixelDepth)
{
	float3 pos = worldPos - _WorldSpaceCameraPos;
	float depthDiff = sceneDepth / pixelDepth;

	pos.xyz *= depthDiff;
	pos.xyz += _WorldSpaceCameraPos;

	return pos;
}

inline float DistanceFade(float depth, float pixelDepth, float start, float end) 
{
	float dist = (abs(depth - pixelDepth) - end) / (start - end);
	return saturate(dist);
}
//==========================================================================================================
// Rim
//==========================================================================================================
inline fixed RimLight (half3 vDir,fixed3 n,fixed rimPower)
{
	return pow(1.0 - saturate(dot(SafeNormalize(vDir),n)),rimPower);
}
//==========================================================================================================
// UnpackNormals blend and scale
//==========================================================================================================
half3 UnpackNormalScale(half4 n1, half scale)
{
#if defined(UNITY_NO_DXT5nm)
	half3 normal = normalize((n1.xyz * 2 - 1));
#if (SHADER_TARGET >= 30)
	normal.xy *= scale;
#endif
	return normal;
#else
	half3 normal;
	normal.xy = (n1.wy * 2 - 1);
#if (SHADER_TARGET >= 30)
	normal.xy *= scale;
#endif
	normal.z = sqrt(1.0 - saturate(dot(normal.xy, normal.xy)));
	return normalize(normal);
#endif
}


// =======================================================
// Displacement
// =======================================================
void Wave (out half3 offs, out half3 nrml, half3 vtx, half4 tileableVtx,half amplitude ,half frequency,half s)
{

	float4 v0 = tileableVtx;
	float4 v1 = v0 + float4(0.05,0,0,0);
	float4 v2 = v0 + float4(0,0,0.05,0);

	float speed = s * _Time.y;
	amplitude *= 0.01;

	v0.y += sin ( speed + (v0.x * frequency )) * amplitude;
	v1.y += sin ( speed + (v1.x * frequency )) * amplitude;
	v2.y += sin ( speed + (v2.x * frequency )) * amplitude;

	v0.y -= cos ( speed + (v0.z * frequency )) * amplitude;
	v1.y -= cos ( speed + (v1.z * frequency )) * amplitude;
	v2.y -= cos ( speed + (v2.z * frequency )) * amplitude;

	v1.y -= (v1.y - v0.y) * (1 - _Smoothing);
	v2.y -= (v2.y - v0.y) * (1 - _Smoothing);

	float3 vna = cross(v2-v0,v1-v0);

	float4 vn 	= mul(float4x4(unity_WorldToObject), float4(vna,0) );
	nrml 		= normalize (vn).xyz;
	offs 		= mul(float4x4(unity_WorldToObject),v0).xyz;
}

half3 GerstnerNormal (half2 xzVtx, half4 amp, half4 freq, half4 speed, half4 dirAB, half4 dirCD) 
{
	half3 nrml = half3(0,2.0,0);

	half4 AB = freq.xxyy * amp.xxyy * dirAB.xyzw;
	half4 CD = freq.zzww * amp.zzww * dirCD.xyzw;
	
	half4 dotABCD = freq.xyzw * half4(dot(dirAB.xy, xzVtx), dot(dirAB.zw, xzVtx), dot(dirCD.xy, xzVtx), dot(dirCD.zw, xzVtx));
	half4 TIME = _Time.yyyy * speed;
	
	half4 COS = cos (dotABCD + TIME);
	
	nrml.x -= dot(COS, half4(AB.xz, CD.xz));
	nrml.z -= dot(COS, half4(AB.yw, CD.yw));
	
	nrml.xz *= _Smoothing;
	nrml = normalize (nrml);

	return nrml;			
}	

half3 GerstnerOffset (half2 xzVtx, half steepness, half4 amp, half4 freq, half4 speed, half4 dirAB, half4 dirCD) 
{
	half3 offsets;
	
	half4 AB = steepness * amp.xxyy * dirAB.xyzw;
	half4 CD = steepness * amp.zzww * dirCD.xyzw;
	
	half4 dotABCD = freq.xyzw * half4(dot(dirAB.xy, xzVtx), dot(dirAB.zw, xzVtx), dot(dirCD.xy, xzVtx), dot(dirCD.zw, xzVtx));
	half4 TIME = _Time.yyyy * speed;
	
	half4 COS = cos (dotABCD + TIME);
	half4 SIN = sin (dotABCD + TIME);
	
	offsets.x = dot(COS, half4(AB.xz, CD.xz));
	offsets.z = dot(COS, half4(AB.yw, CD.yw));
	offsets.y = dot(SIN, amp);

	return offsets;			
}	

void Gerstner (	out half3 offs, out half3 nrml,
				 half3 vtx, half3 tileableVtx, 
				 half4 amplitude, half4 frequency, half4 steepness, 
				 half4 speed, half4 directionAB, half4 directionCD) 
{

		offs = GerstnerOffset(tileableVtx.xz, steepness, amplitude, frequency, speed, directionAB, directionCD);
		nrml = GerstnerNormal(tileableVtx.xz + offs.xz, amplitude, frequency, speed, directionAB, directionCD);							
}

// Texture Displacement
float sampleDisplacementTexture(float2 uv)
{
	uv *= _DisplacementTex_ST.xy * 0.1;
	uv += _DisplacementTex_ST.zw;

	float4 uv1 = float4(uv + frac(_DisplacementSpeed.xy * _Time.x), 0, 0);
	float4 uv2 = float4(uv * float2(0.5, 0.5) - frac(_DisplacementSpeed.zw * _Time.x * 0.5) , 0, 0);

	float wave1 = tex2Dlod(_DisplacementTex, uv1);
	float wave2 = tex2Dlod(_DisplacementTex, uv2);

	float waveMix = wave1 + wave2;

	return waveMix * 2.0 - 1.0;
}

void TextureDisplacement( out half3 offs, out half3 nrml, float4 vtx, float intensity, float vectorLength)
{
	float4 v0 = vtx;
	float4 v1 = v0 + float4(vectorLength, 0.0, 0.0, 0.0);
	float4 v2 = v0 + float4(0.0, 0.0, vectorLength, 0.0);

	float2 v0UV = mul(unity_ObjectToWorld, v0).xz;
	float2 v1UV = mul(unity_ObjectToWorld, v1).xz;
	float2 v2UV = mul(unity_ObjectToWorld, v2).xz;

	v0.y += sampleDisplacementTexture(v0UV) * intensity;
	v1.y += sampleDisplacementTexture(v1UV) * intensity;
	v2.y += sampleDisplacementTexture(v2UV) * intensity;

	offs = v0;
	float3 vn = cross(v2.xyz - v0.xyz, v1.xyz - v0.xyz);
	vn.xz *= _Smoothing.xx;
	nrml = normalize(vn);
}

#endif
