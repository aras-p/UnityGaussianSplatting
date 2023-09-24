#ifndef GAUSSIAN_SPLATTING_HLSL
#define GAUSSIAN_SPLATTING_HLSL

float InvSquareCentered01(float x)
{
    x -= 0.5;
    x *= 0.5;
    x = sqrt(abs(x)) * sign(x);
    return x + 0.5;
}

float3 QuatRotateVector(float3 v, float4 r)
{
    float3 t = 2 * cross(r.xyz, v);
    return v + r.w * t + cross(r.xyz, t);
}

float4 QuatInverse(float4 q)
{
    return rcp(dot(q, q)) * q * float4(-1,-1,-1,1);
}

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
    float3 viewPos = mul(viewMatrix, float4(worldPos, 1)).xyz;

    // this is needed in order for splats that are visible in view but clipped "quite a lot" to work
    float aspect = matrixP._m00 / matrixP._m11;
    float tanFovX = rcp(matrixP._m00);
    float tanFovY = rcp(matrixP._m11 * aspect);
    float limX = 1.3 * tanFovX;
    float limY = 1.3 * tanFovY;
    viewPos.x = clamp(viewPos.x / viewPos.z, -limX, limX) * viewPos.z;
    viewPos.y = clamp(viewPos.y / viewPos.z, -limY, limY) * viewPos.z;

    float focal = screenParams.x * matrixP._m00 / 2;

    float4x4 J = float4x4(
        focal / viewPos.z, 0, -(focal * viewPos.x) / (viewPos.z * viewPos.z), 0,
        0, focal / viewPos.z, -(focal * viewPos.y) / (viewPos.z * viewPos.z), 0,
        0, 0, 0, 0,
        0, 0, 0, 0
    );
    viewMatrix._m03_m13_m23 = 0;
    float4x4 W = viewMatrix;
    float4x4 T = mul(J, W);
    float4x4 V = float4x4(
        cov3d0.x, cov3d0.y, cov3d0.z, 0,
        cov3d0.y, cov3d1.x, cov3d1.y, 0,
        cov3d0.z, cov3d1.y, cov3d1.z, 0,
        0, 0, 0, 0
    );
    float4x4 cov = mul(T, mul(V, transpose(T)));

    // Low pass filter to make each splat at least 1px size.
    cov._m00 += 0.3;
    cov._m11 += 0.3;
    return float3(cov._m00, cov._m01, cov._m11);
}

float3 CalcConic(float3 cov2d)
{
    float det = cov2d.x * cov2d.z - cov2d.y * cov2d.y;
    return float3(cov2d.z, -cov2d.y, cov2d.x) * rcp(det);
}

float2 CalcScreenSpaceDelta(float2 svPositionXY, float2 centerXY, float4 projectionParams)
{
    float2 d = svPositionXY - centerXY;
    d.y *= projectionParams.x;
    return d;
}

float CalcPowerFromConic(float3 conic, float2 d)
{
    return -0.5 * (conic.x * d.x*d.x + conic.z * d.y*d.y) + conic.y * d.x*d.y;
}

// Morton interleaving 16x16 group i.e. by 4 bits of coordinates, based on this thread:
// https://twitter.com/rygorous/status/986715358852608000
// which is simplified version of https://fgiesen.wordpress.com/2009/12/13/decoding-morton-codes/
uint EncodeMorton2D_16x16(uint2 c)
{
    uint t = ((c.y & 0xF) << 8) | (c.x & 0xF); // ----EFGH----ABCD
    t = (t ^ (t << 2)) & 0x3333;               // --EF--GH--AB--CD
    t = (t ^ (t << 1)) & 0x5555;               // -E-F-G-H-A-B-C-D
    return (t | (t >> 7)) & 0xFF;              // --------EAFBGCHD
}
uint2 DecodeMorton2D_16x16(uint t)      // --------EAFBGCHD
{
    t = (t & 0xFF) | ((t & 0xFE) << 7); // -EAFBGCHEAFBGCHD
    t &= 0x5555;                        // -E-F-G-H-A-B-C-D
    t = (t ^ (t >> 1)) & 0x3333;        // --EF--GH--AB--CD
    t = (t ^ (t >> 2)) & 0x0f0f;        // ----EFGH----ABCD
    return uint2(t & 0xF, t >> 8);      // --------EFGHABCD
}


static const float SH_C1 = 0.4886025;
static const float SH_C2[] = { 1.0925484, -1.0925484, 0.3153916, -1.0925484, 0.5462742 };
static const float SH_C3[] = { -0.5900436, 2.8906114, -0.4570458, 0.3731763, -0.4570458, 1.4453057, -0.5900436 };

struct SplatSHData
{
    half3 col, sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, sh10, sh11, sh12, sh13, sh14, sh15;
};

half3 ShadeSH(SplatSHData splat, half3 dir, int shOrder)
{
    dir *= -1;

    half x = dir.x, y = dir.y, z = dir.z;

    // ambient band
    half3 res = splat.col; // col = sh0 * SH_C0 + 0.5 is already precomputed
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

static const uint kTexWidth = 2048;

uint3 SplatIndexToPixelIndex(uint idx)
{
    uint3 res;

    uint2 xy = DecodeMorton2D_16x16(idx);
    uint width = kTexWidth / 16;
    idx >>= 8;
    res.x = (idx % width) * 16 + xy.x;
    res.y = (idx / width) * 16 + xy.y;
    res.z = 0;
    return res;
}

struct SplatBoundsInfo
{
    float4 col;
    float3 pos;
    float3 scl;
};

struct SplatChunkInfo
{
    SplatBoundsInfo boundsMin;
    SplatBoundsInfo boundsMax;
};

StructuredBuffer<SplatChunkInfo> _SplatChunks;

static const uint kChunkSize = 256;

struct SplatData
{
    float3 pos;
    float4 rot;
    float3 scale;
    half opacity;
    SplatSHData sh;
};

// Decode quaternion from a "smallest 3" e.g. 10.10.10.2 format
float4 DecodeRotation(float4 pq)
{
    uint idx = (uint)round(pq.w * 3.0); // note: need to round or index might come out wrong in some formats (e.g. fp16.fp16.fp16.fp16)
    float4 q;
    q.xyz = pq.xyz * sqrt(2.0) - (1.0 / sqrt(2.0));
    q.w = sqrt(1.0 - saturate(dot(q.xyz, q.xyz)));
    if (idx == 0) q = q.wxyz;
    if (idx == 1) q = q.xwyz;
    if (idx == 2) q = q.xywz;
    return q;
}

float3 DecodePacked_6_5_5(uint enc)
{
    return float3(
        (enc & 63) / 63.0,
        ((enc >> 6) & 31) / 31.0,
        ((enc >> 11) & 31) / 31.0);
}

float3 DecodePacked_11_10_11(uint enc)
{
    return float3(
        (enc & 2047) / 2047.0,
        ((enc >> 11) & 1023) / 1023.0,
        ((enc >> 21) & 2047) / 2047.0);
}
float3 DecodePacked_11_10_11(float val)
{
    return DecodePacked_11_10_11(asuint(val));
}

float3 DecodePacked_16_16_16(uint2 enc)
{
    return float3(
        (enc.x & 65535) / 65535.0,
        ((enc.x >> 16) & 65535) / 65535.0,
        (enc.y & 65535) / 65535.0);
}

float4 DecodePacked_10_10_10_2(uint enc)
{
    return float4(
        (enc & 1023) / 1023.0,
        ((enc >> 10) & 1023) / 1023.0,
        ((enc >> 20) & 1023) / 1023.0,
        ((enc >> 30) & 3) / 3.0);
}
float4 DecodePacked_10_10_10_2(float val)
{
    return DecodePacked_10_10_10_2(asuint(val));
}

ByteAddressBuffer _SplatPos;
ByteAddressBuffer _SplatOther;
ByteAddressBuffer _SplatSH;
Texture2D _SplatColor;
uint _SplatFormat;

#define POS_FMT_16 0
#define POS_FMT_11 1
#define POS_FMT_6 2

float3 LoadSplatPos(uint index)
{
    uint fmt = _SplatFormat & 0xFF;
    uint addrU = 0;
    if (fmt == POS_FMT_16)
        addrU = index * 6;
    else if (fmt == POS_FMT_11)
        addrU = index * 4;
    else if (fmt == POS_FMT_6)
        addrU = index * 2;
    uint addrA = addrU & ~0x3;

    uint val0 = _SplatPos.Load(addrA);

    float3 res = 0;
    if (fmt == POS_FMT_16)
    {
        uint val1 = _SplatPos.Load(addrA + 4);
        if (addrU != addrA)
        {
            val0 = (val0 >> 16) | ((val1 & 0xFFFF) << 16);
            val1 >>= 16;
        }
        res = DecodePacked_16_16_16(uint2(val0, val1));
    }
    else if (fmt == POS_FMT_11)
    {
        res = DecodePacked_11_10_11(val0);
    }
    else if (fmt == POS_FMT_6)
    {
        if (addrU != addrA)
            val0 >>= 16;
        res = DecodePacked_6_5_5(val0);
    }

    return res;
}

float4 LoadSplatColTex(uint3 coord)
{
    float4 val = _SplatColor.Load(coord);
    uint fmt = (_SplatFormat >> 24) & 0xFF;
    if (fmt == 49) val = DecodePacked_10_10_10_2(val.r); // R32_SFloat
    return val;
}

float2 Unpack2xFP16(uint v)
{
    return float2(f16tof32(v), f16tof32(v >> 16));
}

SplatData LoadSplatData(uint idx)
{
    SplatData s;
    uint3 coord = SplatIndexToPixelIndex(idx);

    uint chunkIdx = idx / kChunkSize;
    SplatChunkInfo chunk = _SplatChunks[chunkIdx];

    s.pos       = lerp(chunk.boundsMin.pos, chunk.boundsMax.pos, LoadSplatPos(idx));

    uint3 otherData = _SplatOther.Load3(idx * 12);
    s.rot       = DecodeRotation(DecodePacked_10_10_10_2(otherData.x));
    float3 scl = DecodePacked_16_16_16(otherData.yz);
    s.scale     = lerp(chunk.boundsMin.scl, chunk.boundsMax.scl, scl);
    s.scale *= s.scale;
    s.scale *= s.scale;
    s.scale *= s.scale;
    half4 col   = lerp(chunk.boundsMin.col, chunk.boundsMax.col, LoadSplatColTex(coord));
    s.opacity   = InvSquareCentered01(col.a);
    s.sh.col    = col.rgb;

    uint shItemSize = 96; // 15 * 3 * sizeof(fp16), rounded up to be multiple of 16
    uint shIndex = idx;
    uint shFormat = (_SplatFormat >> 16) & 0xFF;
    if (shFormat != 0)
        shIndex = otherData.z >> 16;

    uint shOffset = shIndex * shItemSize;
    uint4 shRaw0 = _SplatSH.Load4(shOffset);
    uint4 shRaw1 = _SplatSH.Load4(shOffset + 16);
    uint4 shRaw2 = _SplatSH.Load4(shOffset + 32);
    uint4 shRaw3 = _SplatSH.Load4(shOffset + 48);
    uint4 shRaw4 = _SplatSH.Load4(shOffset + 64);
    uint3 shRaw5 = _SplatSH.Load3(shOffset + 80);
    s.sh.sh1.r  = f16tof32(shRaw0.x      ); s.sh.sh1.g =  f16tof32(shRaw0.x >> 16); s.sh.sh1.b =  f16tof32(shRaw0.y      );
    s.sh.sh2.r  = f16tof32(shRaw0.y >> 16); s.sh.sh2.g =  f16tof32(shRaw0.z      ); s.sh.sh2.b =  f16tof32(shRaw0.z >> 16);
    s.sh.sh3.r  = f16tof32(shRaw0.w      ); s.sh.sh3.g =  f16tof32(shRaw0.w >> 16); s.sh.sh3.b =  f16tof32(shRaw1.x      );
    s.sh.sh4.r  = f16tof32(shRaw1.x >> 16); s.sh.sh4.g =  f16tof32(shRaw1.y      ); s.sh.sh4.b =  f16tof32(shRaw1.y >> 16);
    s.sh.sh5.r  = f16tof32(shRaw1.z      ); s.sh.sh5.g =  f16tof32(shRaw1.z >> 16); s.sh.sh5.b =  f16tof32(shRaw1.w      );
    s.sh.sh6.r  = f16tof32(shRaw1.w >> 16); s.sh.sh6.g =  f16tof32(shRaw2.x      ); s.sh.sh6.b =  f16tof32(shRaw2.x >> 16);
    s.sh.sh7.r  = f16tof32(shRaw2.y      ); s.sh.sh7.g =  f16tof32(shRaw2.y >> 16); s.sh.sh7.b =  f16tof32(shRaw2.z      );
    s.sh.sh8.r  = f16tof32(shRaw2.z >> 16); s.sh.sh8.g =  f16tof32(shRaw2.w      ); s.sh.sh8.b =  f16tof32(shRaw2.w >> 16);
    s.sh.sh9.r  = f16tof32(shRaw3.x      ); s.sh.sh9.g =  f16tof32(shRaw3.x >> 16); s.sh.sh9.b =  f16tof32(shRaw3.y      );
    s.sh.sh10.r = f16tof32(shRaw3.y >> 16); s.sh.sh10.g = f16tof32(shRaw3.z      ); s.sh.sh10.b = f16tof32(shRaw3.z >> 16);
    s.sh.sh11.r = f16tof32(shRaw3.w      ); s.sh.sh11.g = f16tof32(shRaw3.w >> 16); s.sh.sh11.b = f16tof32(shRaw4.x      );
    s.sh.sh12.r = f16tof32(shRaw4.x >> 16); s.sh.sh12.g = f16tof32(shRaw4.y      ); s.sh.sh12.b = f16tof32(shRaw4.y >> 16);
    s.sh.sh13.r = f16tof32(shRaw4.z      ); s.sh.sh13.g = f16tof32(shRaw4.z >> 16); s.sh.sh13.b = f16tof32(shRaw4.w      );
    s.sh.sh14.r = f16tof32(shRaw4.w >> 16); s.sh.sh14.g = f16tof32(shRaw5.x      ); s.sh.sh14.b = f16tof32(shRaw5.x >> 16);
    s.sh.sh15.r = f16tof32(shRaw5.y      ); s.sh.sh15.g = f16tof32(shRaw5.y >> 16); s.sh.sh15.b = f16tof32(shRaw5.z      );
    return s;
}

struct SplatViewData
{
    float4 pos;
    float4 conicRadius;
    uint2 color; // 4xFP16
};

#endif // GAUSSIAN_SPLATTING_HLSL
