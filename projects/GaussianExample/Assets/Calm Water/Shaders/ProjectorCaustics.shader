Shader "Projector/Caustics" {
	Properties {
		_Color ("Main Color", Color) = (1,1,1,1)
		_CausticTex ("Cookie", 2D) = "" {}
		[Toggle]
		_Scroll("Enable Scrolling",float) = 0
		_Speed("Caustic Speed",float) = 1
		_Blending("Blending",Range(1,2)) = 2
		_FalloffTex ("FallOff", 2D) = "" {}
		[Header(Distortion)]
		_DistortionTex("Distortion Texture",2D) = "black" {} 
		_Distortion ("Distortion",Range(0,1)) = 0.5
		_DistortionSpeed("Distortion Speed",float) = 0.5
	}
	
	Subshader {
		Tags {"Queue"="Transparent"}
		Pass {
			ZWrite Off
			ColorMask RGB
			Blend SrcAlpha One
			Offset -1, -1
	
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#pragma multi_compile_fog
			#pragma multi_compile _ _SCROLL_ON
			#include "UnityCG.cginc"
			
			struct v2f {
				float4 pos : SV_POSITION;

				float4 uvFalloff 	: TEXCOORD0;
				float4 worldPos		: TEXCOORD1;
				float3 worldNormal 	: TEXCOORD2;
				float4 xUV			: TEXCOORD3;
				float4 yUV			: TEXCOORD4;
				float4 zUV			: TEXCOORD5;
			};
			
			float4x4 unity_Projector;
			float4x4 unity_ProjectorClip;
			float _Speed;
			float _Blending;
			float _Distortion;
			float _DistortionSpeed;

			half4 _CausticTex_ST;
			half4 _DistortionTex_ST;
			
			v2f vert (float4 vertex : POSITION, float3 normal : NORMAL)
			{
				v2f o;
				o.pos 			= UnityObjectToClipPos (vertex);
				o.uvFalloff 	= mul (unity_ProjectorClip, vertex);
				o.worldPos		= mul (unity_ObjectToWorld,vertex);
				o.worldNormal 	= UnityObjectToWorldNormal(normal);

				#if _SCROLL_ON
				float time1 	= frac(_Time.x * _Speed);
				float time2 	= frac(_Time.x * _DistortionSpeed);
				#else
				float time1 = 0;
				float time2 = 0;
				#endif

				// Anim UV
				o.xUV.xy	= o.worldPos.zy * _CausticTex_ST.xy + time1;
				o.xUV.zw 	= o.worldPos.zy * _DistortionTex_ST.xy - time2;

				o.yUV.xy	= o.worldPos.xz * _CausticTex_ST.xy + time1;
				o.yUV.zw 	= o.worldPos.xz * _DistortionTex_ST.xy - time2;

				o.zUV.xy	= o.worldPos.xy * _CausticTex_ST.xy + time1;
				o.zUV.zw 	= o.worldPos.xy * _DistortionTex_ST.xy - time2;

				return o;
			}
			
			fixed4 _Color;
			sampler2D _CausticTex;
			sampler2D _FalloffTex;
			sampler2D _DistortionTex;
			
			fixed4 frag (v2f i) : SV_Target
			{
				fixed texF = tex2Dproj (_FalloffTex, UNITY_PROJ_COORD(i.uvFalloff)).a;

				half3 blendWeights = smoothstep(0,_Blending,abs(i.worldNormal));

				float2 offset 	= tex2D (_DistortionTex, i.xUV.zw).rg * blendWeights.x * _Distortion;
				float2 offset2 	= tex2D (_DistortionTex, i.yUV.zw).rg * blendWeights.y * _Distortion;
				float2 offset3 	= tex2D (_DistortionTex, i.zUV.zw).rg * blendWeights.z * _Distortion;
 
				fixed tex 	= tex2D (_CausticTex, i.xUV.xy + offset * 0.5 + 0.5) * blendWeights.x;
				fixed tex2 	= tex2D (_CausticTex, i.yUV.xy + offset2 * 0.5 + 0.5)* blendWeights.y;
				fixed tex3 	= tex2D (_CausticTex, i.zUV.xy + offset3 * 0.5 + 0.5) * blendWeights.z;

//				fixed tex 	= max(tex2D (_CausticTex, i.xUV.xy), tex2D (_CausticTex, i.xUV.zw))	* blendWeights.x;
//				fixed tex2 	= max(tex2D (_CausticTex, i.yUV.xy), tex2D (_CausticTex, i.yUV.zw))	* blendWeights.y;
//				fixed tex3 	= max(tex2D (_CausticTex, i.zUV.xy), tex2D (_CausticTex, i.zUV.zw)) * blendWeights.z;

				fixed res 	= tex + tex2 + tex3;


				//return fixed4(blendWeights,1);
				return 2.0 * fixed4(_Color.rgb,saturate(res) * _Color.a * texF);
			}
			ENDCG
		}
	}
}
