#ifndef CALMWATER_VARIABLES_INCLUDED
#define CALMWATER_VARIABLES_INCLUDED

UNITY_DECLARE_SCREENSPACE_TEXTURE(_GrabTexture);
UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);//Fix for depth precision

sampler2D _MainTex, _BumpMap, _BumpMapLarge, _FlowMap, _CapsMask, _FoamTex, _CausticsTex, _ReflectionTex, _DisplacementTex;
half4 _BumpMap_ST, _BumpMapLarge_ST, _FlowMap_ST, _CapsMask_ST, _FoamTex_ST, _CausticsTex_ST, _DisplacementTex_ST;

samplerCUBE _Cube;

fixed4 _Color, _DepthColor, _CubeColor, _FoamColor, _ScatterColor;

#ifndef LIGHTING_INCLUDED
fixed3 _SpecColor;
uniform fixed4 _LightColor0;
#endif

half4 _GrabTexture_TexelSize;

float _EdgeFade, _BumpStrength, _BumpLargeStrength, _FlowSpeed, _FlowIntensity;
float _DepthStart, _DepthEnd, _Distortion, _Reflection, _ReflectionNormals,_RimPower, _FoamSize, _CapsSpeed, _CapsIntensity, _CapsSize, _CubeDist;
float _CausticsIntensity, _CausticsStart, _CausticsEnd, _CausticsSpeed;
float _Amplitude, _Frequency, _Speed, _Steepness;

float4 _Speeds, _SpeedsLarge, _WSpeed, _WDirectionAB, _WDirectionCD, _ScatterParams, _DisplacementSpeed;

half _Smoothness;
float _Tess, _Smoothing, _specFresnel, _specIntensity;

#ifndef LIGHTCOLOR
#define LIGHTCOLOR
#endif

struct appdata {
    float4 vertex 	: POSITION;
    float3 normal 	: NORMAL;
    float4 tangent 	: TANGENT;
    float2 texcoord : TEXCOORD0;
    float2 texcoord1 : TEXCOORD1;
    float4 color 	: COLOR;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};


// V2F
struct v2f {
    float4 pos : SV_POSITION;
    fixed4 ambient : COLOR;

    half4 tspace0 : TEXCOORD0; // tangent.x, bitangent.x, normal.x
    half4 tspace1 : TEXCOORD1; // tangent.y, bitangent.y, normal.y
    half4 tspace2 : TEXCOORD2; // tangent.z, bitangent.z, normal.z

    float3 worldPos : TEXCOORD3; // w = distance
    float4 GrabUV 	: TEXCOORD4;
    float4 DepthUV	: TEXCOORD5;

#if _BUMPMODE_SINGLE ||_BUMPMODE_DUAL
    float4 AnimUV	: TEXCOORD6;
#endif
#if _BUMPMODE_DUAL
    float2 AnimUV2 	: TEXCOORD7;
#endif
#if _BUMPMODE_FLOWMAP
    float2 BumpUV		: TEXCOORD6;
    float2 FlowMapUV	: TEXCOORD7;
#endif

    //#ifdef UNITY_PASS_FORWARDADD
    UNITY_SHADOW_COORDS(8)
        //#endif

#ifdef UNITY_PASS_FORWARDBASE
        UNITY_FOG_COORDS(9)
#endif

#ifndef SHADER_API_D3D9
#if _FOAM_ON || _WHITECAPS_ON
        float4 FoamUV	: TEXCOORD10;
#endif
#endif

    UNITY_VERTEX_OUTPUT_STEREO
};


struct GlobalData 
{
    float pixelDepth;
    float cleanDepth;
    float depth;
    float4 refractedBuffers;	// RGB: Refraction Color A: Refraction Depth
    float4 cleanBuffers;	    // RGB: Clear Color A: Clean Depth
    float3 worldPosition;
    float3 worldNormal;
    float3 tangentNormal;
    float3 worldViewDir;
    float3 lightDir;
    float4 GrabUV;
    float4 DepthUV;
    float3 finalColor;
    float3 specular;
    float NdotV;

};

void InitializeGlobalData(inout GlobalData data, v2f IN)
{

    data.depth = 0.0;
    data.cleanDepth = 0.0;
    data.pixelDepth = IN.DepthUV.z;


    data.worldPosition = IN.worldPos;
    data.worldNormal = float3(0, 1, 0);
    data.tangentNormal = float3(0, 0, 1);
    // World ViewDir
    data.worldViewDir = normalize(UnityWorldSpaceViewDir(IN.worldPos.xyz));

    #ifdef CULL_FRONT
        data.worldViewDir = -data.worldViewDir;
    #endif


    // World LightDir
    #ifndef USING_DIRECTIONAL_LIGHT
        data.lightDir = normalize(UnityWorldSpaceLightDir(IN.worldPos.xyz));
    #else
        data.lightDir = normalize(_WorldSpaceLightPos0.xyz);
    #endif

    data.refractedBuffers = float4(0, 0, 0, 0);
    data.cleanBuffers = float4(0, 0, 0, 0);
    data.GrabUV = float4(0, 0, 0, 0);
    data.DepthUV = float4(0, 0, 0, 0);
    data.finalColor = float3(0, 0, 0);
    data.specular = float3(0, 0, 0);
    data.NdotV = 0.0;
    //data.screenUV = float4(IN.screenCoord.xyz / IN.screenCoord.w, IN.pos.z);

}

#endif