#ifndef CALMWATER_VERTEX_INCLUDED
#define CALMWATER_VERTEX_INCLUDED


void displacement(inout appdata v)
{

	half4 worldSpaceVertex = mul(unity_ObjectToWorld, (v.vertex));
	half3 offsets;
	half3 nrml;

	#if _DISPLACEMENTMODE_WAVE
		Wave(
			offsets, nrml, v.vertex.xyz, worldSpaceVertex,
			_Amplitude,
			_Frequency,
			_Speed
		);
		v.vertex.y += offsets.y;
		v.normal = nrml;
		v.color.a = offsets.y;

	#endif

	#if _DISPLACEMENTMODE_GERSTNER
		half3 vtxForAni = (worldSpaceVertex.xyz).xzz; // REMOVE VARIABLE
		Gerstner(
			offsets, nrml, v.vertex.xyz, vtxForAni,				// offsets, nrml will be written
			_Amplitude * 0.01,									// amplitude
			_Frequency,											// frequency
			_Steepness,											// steepness
			_WSpeed,											// speed
			_WDirectionAB,										// direction # 1, 2
			_WDirectionCD										// direction # 3, 4									
		);

		v.vertex.xyz += offsets;
		v.normal = nrml;
		v.color.a = offsets.y;

	#endif

	#if _DISPLACEMENTMODE_TEXTURE

		//offs, nrml, vtx, intensity, vectorLength
		TextureDisplacement(offsets, nrml, v.vertex, _Amplitude * 0.02, 1);

		v.vertex.y += offsets.y;
		v.normal = nrml;
		v.color.a = offsets.y;

	#endif

}

// Vertex
v2f vert(appdata v) {
	v2f o;
	UNITY_SETUP_INSTANCE_ID(v);
	UNITY_INITIALIZE_OUTPUT(v2f, o);
	UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

	#if !_DISPLACEMENTMODE_OFF
		displacement(v);
	#endif

	o.pos = UnityObjectToClipPos(v.vertex);
	o.GrabUV = ComputeGrabScreenPos(o.pos);
	o.DepthUV = ComputeScreenPos(o.pos);
	COMPUTE_EYEDEPTH(o.DepthUV.z);

	//Normals
	float4 worldPos = mul(unity_ObjectToWorld, v.vertex);
	float3 worldNormal = UnityObjectToWorldNormal(v.normal);
	float3 worldTangent = UnityObjectToWorldNormal(v.tangent.xyz);
	float3 worldBinormal = cross(worldTangent, worldNormal);

	o.tspace0 = float4(worldTangent.x, worldBinormal.x, worldNormal.x, worldPos.x);
	o.tspace1 = float4(worldTangent.y, worldBinormal.y, worldNormal.y, worldPos.y);
	o.tspace2 = float4(worldTangent.z, worldBinormal.z, worldNormal.z, worldPos.z);
	o.worldPos = worldPos;

	o.ambient.rgb = ShadeSH9(half4(worldNormal, 1));
	o.ambient.a = v.color.a;

	//UV Animation
	#if _BUMPMODE_SINGLE || _BUMPMODE_DUAL
	#if _WORLDSPACE_ON
		o.AnimUV = AnimateBump(worldPos.xz);
	#else
		o.AnimUV = AnimateBump(v.texcoord);
	#endif
	#endif

	#if _BUMPMODE_DUAL
	#if _WORLDSPACE_ON
		o.AnimUV2 = AnimateLargeBump(_BumpMapLarge_ST, worldPos.xz, _SpeedsLarge.xy);
	#else
		o.AnimUV2 = AnimateLargeBump(_BumpMapLarge_ST, v.texcoord, _SpeedsLarge.xy);
	#endif
	#endif
	#if _BUMPMODE_FLOWMAP
	#if _WORLDSPACE_ON
		o.BumpUV = TRANSFORM_TEX(worldPos.xz, _BumpMap);
		o.FlowMapUV = TRANSFORM_TEX(worldPos.xz, _FlowMap);
	#else
		o.BumpUV = TRANSFORM_TEX(v.texcoord, _BumpMap);
		o.FlowMapUV = TRANSFORM_TEX(v.texcoord, _FlowMap);
	#endif
	#endif

		//Foam
	#ifndef SHADER_API_D3D9
	#if _FOAM_ON || _WHITECAPS_ON

		o.FoamUV = float4(0, 0, 0, 0);
		// Shore Foam
	#if _WORLDSPACE_ON
		o.FoamUV.xy = TRANSFORM_TEX(worldPos.xz, _FoamTex);
	#else
		o.FoamUV.xy = TRANSFORM_TEX(v.texcoord, _FoamTex);
	#endif

		// White Caps
	#if _WHITECAPS_ON

	#if _WORLDSPACE_ON
		o.FoamUV.zw = TRANSFORM_TEX(worldPos.xz, _CapsMask);
	#else
		o.FoamUV.zw = TRANSFORM_TEX(v.texcoord, _CapsMask);
	#endif
		// Animate Caps
		o.FoamUV.zw += frac(_CapsSpeed * _Time.x).xx;

	#endif
	#endif
	#endif

	#ifdef UNITY_PASS_FORWARDBASE
		UNITY_TRANSFER_FOG(o, o.pos);
	#endif

		UNITY_TRANSFER_SHADOW(o, v.texcoord1.xy); // pass shadow coordinates to pixel shader

		return o;
	}


#endif