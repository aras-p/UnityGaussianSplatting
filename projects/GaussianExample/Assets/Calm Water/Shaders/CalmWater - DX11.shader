
Shader "CalmWater/Calm Water [DX11]"{
	Properties{
		_Color("Shallow Color",Color) = (1,1,1,1)
		_DepthColor("Depth Color",Color) = (0,0.5,0.5,1)
		_DepthStart("Depth Start",float) = 0
		_DepthEnd("Depth End",float) = 10

		[Toggle(_DEPTHFOG_ON)] _EnableFog("Enable Depth Fog", Float) = 0
		_EdgeFade("Edge Fade",float) = 1

		//Scatter
		[Toggle(_SCATTER_ON)] _Scatter("Enable Scatter", Float) = 0
		_ScatterColor("Scatter Color", color) = (0,1,1,1)
		_ScatterParams("Scatter Parameters", vector) = (1,1,1,1)

		//Spec
		_SpecColor("SpecularColor",Color) = (1,1,1,1)
		_Smoothness("Smoothness",Range(0.1,2)) = 1

		//Second Spec
		_specFresnel("Fresnel", Range(0.1,4)) = 2
		_specIntensity("Intensity", float) = 1
		
		
		//Normal Map
		[KeywordEnum(Single,Dual,FlowMap)]
		_BumpMode("Bump Mode",Float) = 0

		_BumpMap("Micro Detail", 2D) = "bump" {}
		_BumpStrength ("Bump Strength",Range(0,1)) = 1
		_BumpMapLarge("Large Detail", 2D) = "bump" {}
		_BumpLargeStrength ("Bump Large Strength",Range(0,1)) = 1

		//FlowMap
		_FlowMap("FlowMap", 2D) = "grey"{}
		_FlowSpeed("Flow Speed", float) = 1
		_FlowIntensity("FlowIntensity",float) = 1

		//Animation
		[Toggle(_WORLDSPACE_ON)] _WorldSpace ("World UV", Float) = 0
		_Speeds ("Speeds",vector) = (0.5,0.5,0.5,0.5)
		_SpeedsLarge ("Speeds Large",vector) = (0.5,0.5,0,0)

		//[Header(Distortion)]
		//Distortion
		_Distortion("Distortion", Range(0,100) ) = 50.0
		[KeywordEnum(High,Low)]
		_DistortionQuality("Distortion Quality",Float) = 0

		//[Header(Reflection)]
		//Reflection
		[KeywordEnum(None,Mixed,RealTime,CubeMap)] 
		_ReflectionType("ReflectionType", Float)  = 0
		
		_CubeColor("CubeMap Color [RGB] Intensity [A]",Color) = (1,1,1,1)
		[NoScaleOffset]
		_Cube("CubeMap", Cube) = "black" {}
		[NoScaleOffset]
		_ReflectionTex ("Internal reflection", 2D) = "white" {}
		_CubeDist("Cube Distortion",Range(0,1)) = 0.05
		_Reflection("Reflection", Range(0,1) ) = 1
		_RimPower("Fresnel Angle", Range(0,1) ) = 0.5
		_ReflectionNormals("Normals", Range(0,1)) = 1

		//[Header(Foam)]
		//Foam
		[Toggle(_FOAM_ON)] _FOAM ("Enable Foam", Float) = 0
		_FoamColor("FoamColor",Color) = (1,1,1,1)
		_FoamTex("Foam Texture", 2D) = "white" {}
		_FoamSize("Fade Size",float) = 0.5
		[Toggle(_WHITECAPS_ON)] _WhiteCaps ("Enable White Caps", Float) = 0
		_CapsIntensity("Intensity",Range(0,4)) = 1
		_CapsMask("Mask",2D) = "white" {}
		_CapsSpeed("Speed",float) = 1
		_CapsSize("Size",float) = 1

		// Caustics
		[Toggle(_CAUSTICS_ON)] _Caustics("Enable Caustics", Float) = 0
		_CausticsTex("Caustics Texture", 2D) = "black" {}
		_CausticsIntensity("Intensity", float) = 4
		_CausticsStart("Start", float) = 0
		_CausticsEnd("End", float) = 10
		_CausticsSpeed("Speed", float) = 1

		//[Header(Displacement)]
		//Displacement
		[KeywordEnum(Off,Wave,Gerstner,Texture)] 
		_DisplacementMode("Mode", Float)  = 0

		//WaveMode
		_Amplitude("Amplitude", float) = 0.05
		_Frequency("Frequency",float) = 1
		_Speed("Wave Speed", float) = 1

		//GerstnerMode
		_Steepness ("Wave Steepness",float) = 1
		_WSpeed ("Wave Speed", Vector) = (1.2, 1.375, 1.1, 1.5)
		_WDirectionAB ("Wave1 Direction", Vector) = (0.3 ,0.85, 0.85, 0.25)
		_WDirectionCD ("Wave2 Direction", Vector) = (0.1 ,0.9, 0.5, 0.5)

		//Texture Mode
		_DisplacementTex("Displacement Texture", 2D) = "black" {}
		_DisplacementSpeed("Speed Params", vector) = (1,1,1,1)

		//Normal Smoothing
		_Smoothing("Smoothing",range(0,1)) = 1

		//[Header(Tessellation)]
		_Tess ("Tessellation", Range(1,32)) = 4

	}

	Category {
		Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Opaque" "PreviewType"="Sphere" }
		ZWrite On

		SubShader {

	 		GrabPass
			{
				"_GrabTexture"
				Name "BASE"
				Tags { 
					"LightMode" = "Always" 
				}
			}

	        Pass {
	       		Tags {"LightMode" = "ForwardBase"}
	            Name "FORWARD"
	            Cull Back
	 
	            CGPROGRAM
	            #pragma hull hs_surf
	            #pragma domain ds_surf
	            #pragma vertex tessvert
	            #pragma fragment frag
	            #pragma multi_compile_fwdbase
	            #pragma multi_compile_fog
	            #pragma fragmentoption ARB_precision_hint_fastest
	            //#pragma only_renderers d3d11
	            #pragma exclude_renderers d3d9 glcore gles gles3 metal xbox360 ps3
	            #pragma target 5.0
				#ifndef UNITY_PASS_FORWARDBASE
	            #define UNITY_PASS_FORWARDBASE
				#endif
	            #define DX11

	            #include "UnityCG.cginc"
	            #include "AutoLight.cginc"
	            #include "Tessellation.cginc"
	            #include "Includes/CalmWater.cginc"

	            #pragma shader_feature _ _BUMPMODE_SINGLE _BUMPMODE_DUAL _BUMPMODE_FLOWMAP
				#pragma shader_feature _ _REFLECTIONTYPE_MIXED _REFLECTIONTYPE_CUBEMAP _REFLECTIONTYPE_REALTIME 
				#pragma shader_feature _DISTORTIONQUALITY_HIGH
				#pragma shader_feature _ _FOAM_ON
				#pragma shader_feature _ _CAUSTICS_ON
				#pragma shader_feature _ _WHITECAPS_ON
				#pragma shader_feature _ _SCATTER_ON
				#pragma shader_feature _ _DEPTHFOG_ON
				#pragma shader_feature _ _DISPLACEMENTMODE_WAVE _DISPLACEMENTMODE_GERSTNER _DISPLACEMENTMODE_TEXTURE
				#pragma shader_feature _ _WORLDSPACE_ON

	            ENDCG
	        }

	        Pass {
	            Tags {"LightMode" = "ForwardAdd"}
	            Name "FORWARDADD"
	            Blend One One
	            Cull Back
	            CGPROGRAM
	            #pragma hull hs_surf
	            #pragma domain ds_surf
	            #pragma vertex tessvert
	            #pragma fragment frag

	            //#pragma multi_compile_fwdadd
	            #pragma multi_compile_fwdadd_fullshadows  
	            //#pragma multi_compile_fog
	            #pragma fragmentoption ARB_precision_hint_fastest
	            #pragma exclude_renderers d3d9 glcore gles gles3 metal xbox360 ps3
	            //#pragma only_renderers d3d11
	            #pragma target 5.0

	            #define DX11
				#ifndef UNITY_PASS_FORWARDADD
				#define UNITY_PASS_FORWARDADD
				#endif

	            #include "UnityCG.cginc"
	            #include "AutoLight.cginc"
	            #include "Tessellation.cginc"
	            #include "Includes/CalmWater.cginc"

				#pragma shader_feature _ _BUMPMODE_SINGLE _BUMPMODE_DUAL _BUMPMODE_FLOWMAP
				#pragma shader_feature _ _REFLECTIONTYPE_MIXED _REFLECTIONTYPE_CUBEMAP _REFLECTIONTYPE_REALTIME 
				#pragma shader_feature _DISTORTIONQUALITY_HIGH
				#pragma shader_feature _ _FOAM_ON
				#pragma shader_feature _ _DEPTHFOG_ON
				#pragma shader_feature _ _DISPLACEMENTMODE_WAVE _DISPLACEMENTMODE_GERSTNER _DISPLACEMENTMODE_TEXTURE
				#pragma shader_feature _ _WORLDSPACE_ON

	        	ENDCG
	        }
	    }
    }
    CustomEditor "CalmWaterInspector"
    FallBack "CalmWater/Calm Water [Single Sided]"
}
 