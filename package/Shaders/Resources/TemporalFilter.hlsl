// Based on very trimmed down version of Unity's URP TAA,
// https://github.com/Unity-Technologies/Graphics/blob/81a1ed4/Packages/com.unity.render-pipelines.universal/Shaders/PostProcessing/TemporalAA.hlsl
//
// Currently none of motion stuff is used, so this is not much more than
// just a stupid accumulation buffer type of thing.

#include "HLSLSupport.cginc"

#ifndef TAA_YCOCG
#define TAA_YCOCG 1
#endif

#define TAA_GAMMA_SPACE_POST 1 // splats are rendered in sRGB

#ifndef TAA_PERCEPTUAL_SPACE
#define TAA_PERCEPTUAL_SPACE 1
#endif

#define HALF_MIN 6.103515625e-5  // 2^-14, the same value for 10, 11 and 16-bit: https://www.khronos.org/opengl/wiki/Small_Float_Formats

// This function take a rgb color (best is to provide color in sRGB space)
// and return a YCoCg color in [0..1] space for 8bit (An offset is apply in the function)
// Ref: http://www.nvidia.com/object/real-time-ycocg-dxt-compression.html
#define YCOCG_CHROMA_BIAS (128.0 / 255.0)
half3 RGBToYCoCg(half3 rgb)
{
    half3 YCoCg;
    YCoCg.x = dot(rgb, half3(0.25, 0.5, 0.25));
    YCoCg.y = dot(rgb, half3(0.5, 0.0, -0.5)) + YCOCG_CHROMA_BIAS;
    YCoCg.z = dot(rgb, half3(-0.25, 0.5, -0.25)) + YCOCG_CHROMA_BIAS;
    return YCoCg;
}

half3 YCoCgToRGB(half3 YCoCg)
{
    half Y = YCoCg.x;
    half Co = YCoCg.y - YCOCG_CHROMA_BIAS;
    half Cg = YCoCg.z - YCOCG_CHROMA_BIAS;
    half3 rgb;
    rgb.r = Y + Co - Cg;
    rgb.g = Y + Cg;
    rgb.b = Y - Co - Cg;
    return rgb;
}

half Max3(half a, half b, half c) { return max(max(a, b), c); }

Texture2D _CameraDepthTexture;
Texture2D _GaussianSplatRT;
float4 _GaussianSplatRT_TexelSize;
Texture2D _TaaMotionVectorTex;
Texture2D _TaaAccumulationTex;

cbuffer TemporalAAData {
    float4 _TaaMotionVectorTex_TexelSize;   // (1/w, 1/h, w, h)
    float4 _TaaAccumulationTex_TexelSize;   // (1/w, 1/h, w, h)

    half _TaaFrameInfluence;
    half _TaaVarianceClampScale;
}
SamplerState sampler_LinearClamp, sampler_PointClamp;

// Per-pixel camera backwards velocity
half2 GetVelocityWithOffset(float2 uv, half2 depthOffsetUv)
{
    // Unity motion vectors are forward motion vectors in screen UV space
    half2 offsetUv = _TaaMotionVectorTex.Sample(sampler_LinearClamp, uv + _TaaMotionVectorTex_TexelSize.xy * depthOffsetUv).xy;
    return -offsetUv;
}

void AdjustBestDepthOffset(inout half bestDepth, inout half bestX, inout half bestY, float2 uv, half currX, half currY)
{
    // Half precision should be fine, as we are only concerned about choosing the better value along sharp edges, so it's
    // acceptable to have banding on continuous surfaces
    half depth = _CameraDepthTexture.Sample(sampler_PointClamp, uv.xy + _GaussianSplatRT_TexelSize.xy * half2(currX, currY)).r;

#if UNITY_REVERSED_Z
    depth = 1.0 - depth;
#endif

    bool isBest = depth < bestDepth;
    bestDepth = isBest ? depth : bestDepth;
    bestX = isBest ? currX : bestX;
    bestY = isBest ? currY : bestY;
}

float GetLuma(float3 color)
{
#if TAA_YCOCG
    // We work in YCoCg hence the luminance is in the first channel.
    return color.x;
#else
    return Luminance(color.xyz);
#endif
}

float PerceptualWeight(float3 c)
{
#if TAA_PERCEPTUAL_SPACE
    return rcp(GetLuma(c) + 1.0);
#else
    return 1;
#endif
}

float PerceptualInvWeight(float3 c)
{
#if TAA_PERCEPTUAL_SPACE
    return rcp(1.0 - GetLuma(c));
#else
    return 1;
#endif
}

float4 WorkingToPerceptual(float4 c)
{
    float scale = PerceptualWeight(c.xyz);
    return c * scale;
}

float4 PerceptualToWorking(float4 c)
{
    float scale = PerceptualInvWeight(c.xyz);
    return c * scale;
}

half4 PostFxSpaceToLinear(float4 src)
{
// gamma 2.0 is a good enough approximation
#if TAA_GAMMA_SPACE_POST
    return half4(src.xyz * src.xyz, src.w);
#else
    return src;
#endif
}

half4 LinearToPostFxSpace(float4 src)
{
#if TAA_GAMMA_SPACE_POST
    return half4(sqrt(src.xyz), src.w);
#else
    return src;
#endif
}

// Working Space: The color space that we will do the calculation in.
// Scene: The incoming/outgoing scene color. Either linear or gamma space
half4 SceneToWorkingSpace(half4 src)
{
    half4 linColor = PostFxSpaceToLinear(src);
#if TAA_YCOCG
    half4 dst = half4(RGBToYCoCg(linColor.xyz), linColor.w);
#else
    half4 dst = src;
#endif
    return dst;
}

half4 WorkingSpaceToScene(half4 src)
{
#if TAA_YCOCG
    half4 linColor = half4(YCoCgToRGB(src.xyz), src.w);
#else
    half4 linColor = src;
#endif

    half4 dst = LinearToPostFxSpace(linColor);
    return dst;
}

half4 SampleColorPoint(float2 uv, int2 texelOffset)
{
    return _GaussianSplatRT.Sample(sampler_PointClamp, uv, texelOffset);
}

void AdjustColorBox(inout half4 boxMin, inout half4 boxMax, inout half4 moment1, inout half4 moment2, float2 uv, int2 offset)
{
    half4 color = SceneToWorkingSpace(SampleColorPoint(uv, offset));
    boxMin = min(color, boxMin);
    boxMax = max(color, boxMax);
    moment1 += color;
    moment2 += color * color;
}

half4 ApplyHistoryColorLerp(half4 workingAccumColor, half4 workingCenterColor, float t)
{
    half4 perceptualAccumColor = WorkingToPerceptual(workingAccumColor);
    half4 perceptualCenterColor = WorkingToPerceptual(workingCenterColor);

    half4 perceptualDstColor = lerp(perceptualAccumColor, perceptualCenterColor, t);
    half4 workingDstColor = PerceptualToWorking(perceptualDstColor);

    return workingDstColor;
}

// From Filmic SMAA presentation[Jimenez 2016]
// A bit more verbose that it needs to be, but makes it a bit better at latency hiding
// (half version based on HDRP impl)
half4 SampleBicubic5TapHalf(Texture2D sourceTexture, float2 UV, float4 sourceTexture_TexelSize)
{
    const float2 sourceTextureSize = sourceTexture_TexelSize.zw;
    const float2 sourceTexelSize = sourceTexture_TexelSize.xy;

    float2 samplePos = UV * sourceTextureSize;
    float2 tc1 = floor(samplePos - 0.5) + 0.5;
    half2 f = samplePos - tc1;
    half2 f2 = f * f;
    half2 f3 = f * f2;

    half c = 0.5;

    half2 w0 = -c         * f3 +  2.0 * c         * f2 - c * f;
    half2 w1 =  (2.0 - c) * f3 - (3.0 - c)        * f2          + 1.0;
    half2 w2 = -(2.0 - c) * f3 + (3.0 - 2.0 * c)  * f2 + c * f;
    half2 w3 = c          * f3 - c                * f2;

    half2 w12 = w1 + w2;
    float2 tc0 = sourceTexelSize  * (tc1 - 1.0);
    float2 tc3 = sourceTexelSize  * (tc1 + 2.0);
    float2 tc12 = sourceTexelSize * (tc1 + w2 / w12);

    half4 s0 = SceneToWorkingSpace(sourceTexture.Sample(sampler_LinearClamp, float2(tc12.x, tc0.y)));
    half4 s1 = SceneToWorkingSpace(sourceTexture.Sample(sampler_LinearClamp, float2(tc0.x, tc12.y)));
    half4 s2 = SceneToWorkingSpace(sourceTexture.Sample(sampler_LinearClamp, float2(tc12.x, tc12.y)));
    half4 s3 = SceneToWorkingSpace(sourceTexture.Sample(sampler_LinearClamp, float2(tc3.x, tc12.y)));
    half4 s4 = SceneToWorkingSpace(sourceTexture.Sample(sampler_LinearClamp, float2(tc12.x, tc3.y)));

    half cw0 = (w12.x * w0.y);
    half cw1 = (w0.x * w12.y);
    half cw2 = (w12.x * w12.y);
    half cw3 = (w3.x * w12.y);
    half cw4 = (w12.x *  w3.y);

    s0 *= cw0;
    s1 *= cw1;
    s2 *= cw2;
    s3 *= cw3;
    s4 *= cw4;

    half4 historyFiltered = s0 + s1 + s2 + s3 + s4;
    half weightSum = cw0 + cw1 + cw2 + cw3 + cw4;

    half4 filteredVal = historyFiltered * rcp(weightSum);

    return filteredVal;
}

// From Playdead's TAA
// (half version of HDRP impl)
//
// Small color-volume min size seems to produce flicker/noise in YCoCg space, that can't be seen in RGB,
// when using low precision (RGB111110f) color textures.
half4 ClipToAABBCenter(half4 history, half4 minimum, half4 maximum)
{
    // note: only clips towards aabb center (but fast!)
    half4 center  = 0.5 * (maximum + minimum);
    half4 extents = max(0.5 * (maximum - minimum), HALF_MIN);   // Epsilon to avoid precision issues with empty volume.

    // This is actually `distance`, however the keyword is reserved
    half4 offset = history - center;
    half3 v_unit = offset.xyz / extents.xyz;
    half3 absUnit = abs(v_unit);
    half maxUnit = Max3(absUnit.x, absUnit.y, absUnit.z);
    if (maxUnit > 1.0)
        return center + (offset / maxUnit);
    else
        return history;
}

// clampQuality:
//     0: Cross (5 taps)
//     1: 3x3 (9 taps)
//     2: Variance + MinMax 3x3 (9 taps)
//     3: Variance Clipping
//
// motionQuality:
//     0: None
//     1: 5 taps
//     2: 9 taps
// historyQuality:
//     0: Bilinear
//     1: Bilinear + discard history for UVs out of buffer
//     2: Bicubic (5 taps)
half4 DoTemporalAA(float2 uv, int clampQuality, int motionQuality, int historyQuality)
{
    half4 colorCenter = SceneToWorkingSpace(SampleColorPoint(uv, int2(0,0)));

    half4 boxMax = colorCenter;
    half4 boxMin = colorCenter;
    half4 moment1 = colorCenter;
    half4 moment2 = colorCenter * colorCenter;

    AdjustColorBox(boxMin, boxMax, moment1, moment2, uv, int2(0,-1));
    AdjustColorBox(boxMin, boxMax, moment1, moment2, uv, int2(-1,0));
    AdjustColorBox(boxMin, boxMax, moment1, moment2, uv, int2(1,0));
    AdjustColorBox(boxMin, boxMax, moment1, moment2, uv, int2(0,1));

    if (clampQuality >= 1)
    {
        AdjustColorBox(boxMin, boxMax, moment1, moment2, uv, int2(-1,-1));
        AdjustColorBox(boxMin, boxMax, moment1, moment2, uv, int2(1,-1));
        AdjustColorBox(boxMin, boxMax, moment1, moment2, uv, int2(-1,1));
        AdjustColorBox(boxMin, boxMax, moment1, moment2, uv, int2(1,1));
    }

    if(clampQuality >= 2)
    {
        half perSample = 1 / half(9);
        half4 mean = moment1 * perSample;
        half4 stdDev = sqrt(abs(moment2 * perSample - mean * mean));

        half devScale = _TaaVarianceClampScale;
        half4 devMin = mean - devScale * stdDev;
        half4 devMax = mean + devScale * stdDev;

        // Ensure that the variance color box is not worse than simple neighborhood color box.
        boxMin = max(boxMin, devMin);
        boxMax = min(boxMax, devMax);
    }

    /* @TODO motion stuff
    half bestOffsetX = 0.0f;
    half bestOffsetY = 0.0f;
    half bestDepth = 1.0f;
    if (motionQuality >= 1)
    {
        AdjustBestDepthOffset(bestDepth, bestOffsetX, bestOffsetY, uv, 0.0f, 0.0f);
        AdjustBestDepthOffset(bestDepth, bestOffsetX, bestOffsetY, uv, 1.0f, 0.0f);
        AdjustBestDepthOffset(bestDepth, bestOffsetX, bestOffsetY, uv, 0.0f, -1.0f);
        AdjustBestDepthOffset(bestDepth, bestOffsetX, bestOffsetY, uv, -1.0f, 0.0f);
        AdjustBestDepthOffset(bestDepth, bestOffsetX, bestOffsetY, uv, 0.0f, 1.0f);
    }
    if (motionQuality >= 2)
    {
        AdjustBestDepthOffset(bestDepth, bestOffsetX, bestOffsetY, uv, -1.0f, -1.0f);
        AdjustBestDepthOffset(bestDepth, bestOffsetX, bestOffsetY, uv, 1.0f, -1.0f);
        AdjustBestDepthOffset(bestDepth, bestOffsetX, bestOffsetY, uv, -1.0f, 1.0f);
        AdjustBestDepthOffset(bestDepth, bestOffsetX, bestOffsetY, uv, 1.0f, 1.0f);
    }

    half2 depthOffsetUv = half2(bestOffsetX, bestOffsetY);
    half2 velocity = GetVelocityWithOffset(uv, depthOffsetUv);

    float2 historyUv = uv + velocity * float2(1, 1);
    */

    float2 historyUv = uv; //@TODO: for now assume no motion at all

    half4 accumulation = (historyQuality >= 2) ?
        SampleBicubic5TapHalf(_TaaAccumulationTex, historyUv, _TaaAccumulationTex_TexelSize.xyzw) :
        SceneToWorkingSpace(_TaaAccumulationTex.Sample(sampler_LinearClamp, historyUv));

    half4 clampedAccumulation = (clampQuality >= 3) ? ClipToAABBCenter(accumulation, boxMin, boxMax) : clamp(accumulation, boxMin, boxMax);

    half frameInfluence = _TaaFrameInfluence;

    half4 workingColor = ApplyHistoryColorLerp(clampedAccumulation, colorCenter, frameInfluence);

    half4 dstSceneColor = WorkingSpaceToScene(workingColor);

    return half4(max(dstSceneColor, 0.0));
}
