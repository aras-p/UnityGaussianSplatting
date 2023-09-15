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

Texture2D _TexPos;
Texture2D _TexRot;
Texture2D _TexScl;
Texture2D _TexCol;
Texture2D _TexSH1;
Texture2D _TexSH2;
Texture2D _TexSH3;
Texture2D _TexSH4;
Texture2D _TexSH5;
Texture2D _TexSH6;
Texture2D _TexSH7;
Texture2D _TexSH8;
Texture2D _TexSH9;
Texture2D _TexSHA;
Texture2D _TexSHB;
Texture2D _TexSHC;
Texture2D _TexSHD;
Texture2D _TexSHE;
Texture2D _TexSHF;
uint _TexFlagBits;

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
    float3 shs;
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

float3 DecodePacked_11_10_11(float val)
{
    uint enc = asuint(val);
    return float3(
        (enc & 2047) / 2047.0,
        ((enc >> 11) & 1023) / 1023.0,
        ((enc >> 21) & 2047) / 2047.0);
}

float4 DecodePacked_10_10_10_2(float val)
{
    uint enc = asuint(val);
    return float4(
        (enc & 1023) / 1023.0,
        ((enc >> 10) & 1023) / 1023.0,
        ((enc >> 20) & 1023) / 1023.0,
        ((enc >> 30) & 3) / 3.0);
}

float3 LoadSplatPosTex(uint3 coord)
{
    float3 val = _TexPos.Load(coord).rgb;
    if (_TexFlagBits & 1) val = DecodePacked_11_10_11(val.r);
    return val;
}
float4 LoadSplatRotTex(uint3 coord)
{
    float4 val = _TexRot.Load(coord);
    if (_TexFlagBits & 2) val = DecodePacked_10_10_10_2(val.r);
    return val;
}
float3 LoadSplatSclTex(uint3 coord)
{
    float3 val = _TexScl.Load(coord).rgb;
    if (_TexFlagBits & 4) val = DecodePacked_11_10_11(val.r);
    return val;
}
float4 LoadSplatColTex(uint3 coord)
{
    float4 val = _TexCol.Load(coord);
    if (_TexFlagBits & 8) val = DecodePacked_10_10_10_2(val.r);
    return val;
}
float3 LoadSplatShTex(uint3 coord, Texture2D tex)
{
    float3 val = tex.Load(coord).rgb;
    if (_TexFlagBits & 16) val = DecodePacked_11_10_11(val.r);
    return val;
}

SplatData LoadSplatData(uint idx)
{
    SplatData s;
    uint3 coord = SplatIndexToPixelIndex(idx);

    uint chunkIdx = idx / kChunkSize;
    SplatChunkInfo chunk = _SplatChunks[chunkIdx];
    float3 shMin = chunk.boundsMin.shs;
    float3 shMax = chunk.boundsMax.shs; 

    s.pos       = lerp(chunk.boundsMin.pos, chunk.boundsMax.pos, LoadSplatPosTex(coord));
    s.rot       = DecodeRotation(LoadSplatRotTex(coord));
    s.scale     = lerp(chunk.boundsMin.scl, chunk.boundsMax.scl, LoadSplatSclTex(coord));
    s.scale *= s.scale;
    s.scale *= s.scale;
    s.scale *= s.scale;
    half4 col   = lerp(chunk.boundsMin.col, chunk.boundsMax.col, LoadSplatColTex(coord));
    s.opacity   = InvSquareCentered01(col.a);
    s.sh.col    = col.rgb;
    s.sh.sh1    = lerp(shMin, shMax, LoadSplatShTex(coord, _TexSH1));
    s.sh.sh2    = lerp(shMin, shMax, LoadSplatShTex(coord, _TexSH2));
    s.sh.sh3    = lerp(shMin, shMax, LoadSplatShTex(coord, _TexSH3));
    s.sh.sh4    = lerp(shMin, shMax, LoadSplatShTex(coord, _TexSH4));
    s.sh.sh5    = lerp(shMin, shMax, LoadSplatShTex(coord, _TexSH5));
    s.sh.sh6    = lerp(shMin, shMax, LoadSplatShTex(coord, _TexSH6));
    s.sh.sh7    = lerp(shMin, shMax, LoadSplatShTex(coord, _TexSH7));
    s.sh.sh8    = lerp(shMin, shMax, LoadSplatShTex(coord, _TexSH8));
    s.sh.sh9    = lerp(shMin, shMax, LoadSplatShTex(coord, _TexSH9));
    s.sh.sh10   = lerp(shMin, shMax, LoadSplatShTex(coord, _TexSHA));
    s.sh.sh11   = lerp(shMin, shMax, LoadSplatShTex(coord, _TexSHB));
    s.sh.sh12   = lerp(shMin, shMax, LoadSplatShTex(coord, _TexSHC));
    s.sh.sh13   = lerp(shMin, shMax, LoadSplatShTex(coord, _TexSHD));
    s.sh.sh14   = lerp(shMin, shMax, LoadSplatShTex(coord, _TexSHE));
    s.sh.sh15   = lerp(shMin, shMax, LoadSplatShTex(coord, _TexSHF));
    return s;
}

SplatData LoadSplatDataRaw(uint2 coord2)
{
    SplatData s;
    uint3 coord = uint3(coord2, 0);

    s.pos       = LoadSplatPosTex(coord);
    s.rot       = float4(LoadSplatRotTex(coord).rgb, 1);
    s.scale     = LoadSplatSclTex(coord);
    half4 col   = LoadSplatColTex(coord);
    s.opacity   = col.a;
    s.sh.col    = col.rgb;
    s.sh.sh1    = LoadSplatShTex(coord, _TexSH1);
    s.sh.sh2    = LoadSplatShTex(coord, _TexSH2);
    s.sh.sh3    = LoadSplatShTex(coord, _TexSH3);
    s.sh.sh4    = LoadSplatShTex(coord, _TexSH4);
    s.sh.sh5    = LoadSplatShTex(coord, _TexSH5);
    s.sh.sh6    = LoadSplatShTex(coord, _TexSH6);
    s.sh.sh7    = LoadSplatShTex(coord, _TexSH7);
    s.sh.sh8    = LoadSplatShTex(coord, _TexSH8);
    s.sh.sh9    = LoadSplatShTex(coord, _TexSH9);
    s.sh.sh10   = LoadSplatShTex(coord, _TexSHA);
    s.sh.sh11   = LoadSplatShTex(coord, _TexSHB);
    s.sh.sh12   = LoadSplatShTex(coord, _TexSHC);
    s.sh.sh13   = LoadSplatShTex(coord, _TexSHD);
    s.sh.sh14   = LoadSplatShTex(coord, _TexSHE);
    s.sh.sh15   = LoadSplatShTex(coord, _TexSHF);
    return s;
}

#endif // GAUSSIAN_SPLATTING_HLSL
