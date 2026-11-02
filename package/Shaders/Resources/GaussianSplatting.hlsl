// SPDX-License-Identifier: MIT
#ifndef GAUSSIAN_SPLATTING_HLSL
#define GAUSSIAN_SPLATTING_HLSL

float3x3 CalcMatrixFromRotationScale(float4 rot, float3 scale)
{
    float3x3 ms = float3x3(
        scale.x, 0, 0,
        0, scale.y, 0,
        0, 0, scale.z
    );
    float x = rot.x;
    float y = rot.y;
    float z = rot.z;
    float w = rot.w;
    float3x3 mr = float3x3(
        1-2*(y*y + z*z),   2*(x*y - w*z),   2*(x*z + w*y),
          2*(x*y + w*z), 1-2*(x*x + z*z),   2*(y*z - w*x),
          2*(x*z - w*y),   2*(y*z + w*x), 1-2*(x*x + y*y)
    );
    return mul(mr, ms);
}

void CalcCovariance3D(float3x3 rotMat, out float3 sigma0, out float3 sigma1)
{
    float3x3 sig = mul(rotMat, transpose(rotMat));
    sigma0 = float3(sig._m00, sig._m01, sig._m02);
    sigma1 = float3(sig._m11, sig._m12, sig._m22);
}

// from "EWA Splatting" (Zwicker et al 2002) eq. 31
float3 CalcCovariance2D(float3 worldPos, float3 cov3d0, float3 cov3d1, float4x4 matrixV, float4x4 matrixP, float4 screenParams)
{
    float4x4 viewMatrix = matrixV;
    float focal = screenParams.x * matrixP._m00 / 2;

    bool isOrthographic = matrixP._m33 == 1.0;
    float3x3 J;
    if (isOrthographic)
    {
        J = float3x3(
            focal, 0, 0,
            0, focal, 0,
            0, 0, 0
        );
    }
    else
    {
        float3 viewPos = mul(viewMatrix, float4(worldPos, 1)).xyz;

        // this is needed in order for splats that are visible in view but clipped "quite a lot" to work
        float aspect = matrixP._m00 / matrixP._m11;
        float tanFovX = rcp(matrixP._m00);
        float tanFovY = rcp(matrixP._m11 * aspect);
        float limX = 1.3 * tanFovX;
        float limY = 1.3 * tanFovY;
        viewPos.x = clamp(viewPos.x / viewPos.z, -limX, limX) * viewPos.z;
        viewPos.y = clamp(viewPos.y / viewPos.z, -limY, limY) * viewPos.z;

        J = float3x3(
            focal / viewPos.z, 0, -(focal * viewPos.x) / (viewPos.z * viewPos.z),
            0, focal / viewPos.z, -(focal * viewPos.y) / (viewPos.z * viewPos.z),
            0, 0, 0
        );
    }
    
    float3x3 W = (float3x3)viewMatrix;
    float3x3 T = mul(J, W);
    float3x3 V = float3x3(
        cov3d0.x, cov3d0.y, cov3d0.z,
        cov3d0.y, cov3d1.x, cov3d1.y,
        cov3d0.z, cov3d1.y, cov3d1.z
    );
    float3x3 cov = mul(T, mul(V, transpose(T)));

    // Low pass filter to make each splat at least 1px size.
    cov._m00 += 0.3;
    cov._m11 += 0.3;
    return float3(cov._m00, cov._m01, cov._m11);
}

static const float SH_C1 = 0.4886025;
static const float SH_C2[] = { 1.0925484, -1.0925484, 0.3153916, -1.0925484, 0.5462742 };
static const float SH_C3[] = { -0.5900436, 2.8906114, -0.4570458, 0.3731763, -0.4570458, 1.4453057, -0.5900436 };

struct SplatSHData
{
    half3 col, sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, sh10, sh11, sh12, sh13, sh14, sh15;
};

half3 ShadeSH(SplatSHData splat, half3 dir, int shOrder, bool onlySH)
{
    dir *= -1;

    half x = dir.x, y = dir.y, z = dir.z;

    // ambient band
    half3 res = splat.col; // col = sh0 * SH_C0 + 0.5 is already precomputed
    if (onlySH)
        res = 0.5;
    // 1st degree
    if (shOrder >= 1)
    {
        res += SH_C1 * (-splat.sh1 * y + splat.sh2 * z - splat.sh3 * x);
        // 2nd degree
        if (shOrder >= 2)
        {
            half xx = x * x, yy = y * y, zz = z * z;
            half xy = x * y, yz = y * z, xz = x * z;
            res +=
                (SH_C2[0] * xy) * splat.sh4 +
                (SH_C2[1] * yz) * splat.sh5 +
                (SH_C2[2] * (2 * zz - xx - yy)) * splat.sh6 +
                (SH_C2[3] * xz) * splat.sh7 +
                (SH_C2[4] * (xx - yy)) * splat.sh8;
            // 3rd degree
            if (shOrder >= 3)
            {
                res +=
                    (SH_C3[0] * y * (3 * xx - yy)) * splat.sh9 +
                    (SH_C3[1] * xy * z) * splat.sh10 +
                    (SH_C3[2] * y * (4 * zz - xx - yy)) * splat.sh11 +
                    (SH_C3[3] * z * (2 * zz - 3 * xx - 3 * yy)) * splat.sh12 +
                    (SH_C3[4] * x * (4 * zz - xx - yy)) * splat.sh13 +
                    (SH_C3[5] * z * (xx - yy)) * splat.sh14 +
                    (SH_C3[6] * x * (xx - 3 * yy)) * splat.sh15;
            }
        }
    }
    return max(res, 0);
}

struct SplatData
{
    float3 pos;
    float4 rot;
    float3 scale;
    half opacity;
    SplatSHData sh;
};

// Quaternion encoding matches SPZ v3: largest index and three sign/magnitude components.
float4 DecodeSpzSmallest3Rotation(uint packed)
{
    uint index = packed >> 30;
    uint3 components = uint3(packed >> 20, packed >> 10, packed) & 1023;
    float4 q;
    q.xyz = (components & 511) * (0.7071067811865475 / 511.0);
    q.xyz *= float3((components.x & 512) != 0 ? -1 : 1,
        (components.y & 512) != 0 ? -1 : 1, (components.z & 512) != 0 ? -1 : 1);
    q.w = sqrt(max(0.0, 1.0 - dot(q.xyz, q.xyz)));
    if (index == 0) q = q.wxyz;
    if (index == 1) q = q.xwyz;
    if (index == 2) q = q.xywz;
    return q;
}

#ifdef SHADER_STAGE_COMPUTE
#define SplatBufferDataType RWByteAddressBuffer
#else
#define SplatBufferDataType ByteAddressBuffer
#endif

SplatBufferDataType _SplatPos;
SplatBufferDataType _SplatOther;
SplatBufferDataType _SplatSH;
SplatBufferDataType _SplatColor;
uint _SHCoefficients;

float3 LoadSplatPos(uint idx)
{
    return asfloat(_SplatPos.Load3(idx * 12));
}

float3 LoadSplatSH(uint baseOffset, uint coefficient, float range)
{
    // Each signed RGB565 coefficient occupies an aligned half-word.
    uint address = baseOffset + 2 + coefficient * 2;
    uint shift = (address & 3) * 8;
    address &= ~3u;
    uint v = _SplatSH.Load(address) >> shift;
    int3 signedValue = int3((int)(v << 27) >> 27, (int)(v << 21) >> 26, (int)(v << 16) >> 27);
    return signedValue * (range / float3(15, 31, 15));
}

SplatData LoadSplatGeometry(uint idx)
{
    SplatData s = (SplatData)0;
    s.pos = LoadSplatPos(idx);
    uint3 geometry = _SplatOther.Load3(idx * 12);
    s.rot = DecodeSpzSmallest3Rotation(geometry.x);
    s.scale = float3(f16tof32(geometry.y), f16tof32(geometry.y >> 16), f16tof32(geometry.z));
    s.opacity = f16tof32(geometry.z >> 16);
    return s;
}

void LoadSplatColor(uint idx, inout SplatData s)
{
    uint color = _SplatColor.Load(idx * 4);
    float colorScale = asfloat(((color >> 30) + 116u) << 23); // 2^(exponent - 11)
    s.sh.col = float3(color & 1023u, (color >> 10) & 1023u, (color >> 20) & 1023u) * colorScale - 0.3125;
    if (_SHCoefficients > 0)
    {
        uint offset = idx * ((_SHCoefficients * 2 + 5) / 4) * 4;
        float range = f16tof32(_SplatSH.Load(offset));
        if (_SHCoefficients >= 3)
        {
            s.sh.sh1 = LoadSplatSH(offset, 0, range);
            s.sh.sh2 = LoadSplatSH(offset, 1, range);
            s.sh.sh3 = LoadSplatSH(offset, 2, range);
        }
        if (_SHCoefficients >= 8)
        {
            s.sh.sh4 = LoadSplatSH(offset, 3, range);
            s.sh.sh5 = LoadSplatSH(offset, 4, range);
            s.sh.sh6 = LoadSplatSH(offset, 5, range);
            s.sh.sh7 = LoadSplatSH(offset, 6, range);
            s.sh.sh8 = LoadSplatSH(offset, 7, range);
        }
        if (_SHCoefficients >= 15)
        {
            s.sh.sh9 = LoadSplatSH(offset, 8, range);
            s.sh.sh10 = LoadSplatSH(offset, 9, range);
            s.sh.sh11 = LoadSplatSH(offset, 10, range);
            s.sh.sh12 = LoadSplatSH(offset, 11, range);
            s.sh.sh13 = LoadSplatSH(offset, 12, range);
            s.sh.sh14 = LoadSplatSH(offset, 13, range);
            s.sh.sh15 = LoadSplatSH(offset, 14, range);
        }
    }
}

SplatData LoadSplatData(uint idx)
{
    SplatData s = LoadSplatGeometry(idx);
    LoadSplatColor(idx, s);
    return s;
}

struct SplatViewData
{
    float3 pos; // NDC center; +infinity in x marks a culled splat
    uint axis1, axis2; // each axis is FP16 .x in low bits, .y in high bits
    uint2 color; // 4xFP16
};

// If we are rendering into backbuffer directly (e.g. HDR off, no postprocessing),
// the color target texture is a render target (so projection is upside down),
// but the depth buffer we get is not upside down. We want to flip
// our rendering upside down manually for this case.
//
// There does not seem to be a good way to detect this situation in Unity; work around it
// by setting _CameraTargetTexture global texture to BuiltinRenderTextureType.CameraTarget
// from the command buffer. When CameraTarget will be null (i.e. backbuffer), the _TexeSize
// property of the texture will get set to (1,1,1,1).
//
// One could hope someday Unity will fix all this upside-down thingy...
float4 _CameraTargetTexture_TexelSize;
void FlipProjectionIfBackbuffer(inout float4 vpos)
{
    if (_CameraTargetTexture_TexelSize.z == 1.0)
        vpos.y = -vpos.y;
}

#endif // GAUSSIAN_SPLATTING_HLSL
