// SPDX-License-Identifier: MIT

#ifndef SPHERICAL_HARMONICS_HLSL
#define SPHERICAL_HARMONICS_HLSL

// SH rotation based on https://github.com/andrewwillmott/sh-lib (Unlicense / public domain)

#define SH_MAX_ORDER 4
#define SH_MAX_COEFFS_COUNT (SH_MAX_ORDER*SH_MAX_ORDER)

float3 Dot3(int vidx, float3 v[SH_MAX_COEFFS_COUNT], float f[3])
{
    return v[vidx+0] * f[0] + v[vidx+1] * f[1] + v[vidx+2] * f[2];
}
float3 Dot5(int vidx, float3 v[SH_MAX_COEFFS_COUNT], float f[5])
{
    return v[vidx+0] * f[0] + v[vidx+1] * f[1] + v[vidx+2] * f[2] + v[vidx+3] * f[3] + v[vidx+4] * f[4];
}
float3 Dot7(int vidx, float3 v[SH_MAX_COEFFS_COUNT], float f[7])
{
    return v[vidx+0] * f[0] + v[vidx+1] * f[1] + v[vidx+2] * f[2] + v[vidx+3] * f[3] + v[vidx+4] * f[4] + v[vidx+5] * f[5] + v[vidx+6] * f[6];
}

void RotateSH(float3x3 orient, int n, float3 coeffsIn[SH_MAX_COEFFS_COUNT], out float3 coeffs[SH_MAX_COEFFS_COUNT])
{
    const float kSqrt03_02    = sqrt( 3.0 /  2.0);
    const float kSqrt01_03    = sqrt( 1.0 /  3.0);
    const float kSqrt02_03    = sqrt( 2.0 /  3.0);
    const float kSqrt04_03    = sqrt( 4.0 /  3.0);
    const float kSqrt01_04    = sqrt( 1.0 /  4.0);
    const float kSqrt03_04    = sqrt( 3.0 /  4.0);
    const float kSqrt01_05    = sqrt( 1.0 /  5.0);
    const float kSqrt03_05    = sqrt( 3.0 /  5.0);
    const float kSqrt06_05    = sqrt( 6.0 /  5.0);
    const float kSqrt08_05    = sqrt( 8.0 /  5.0);
    const float kSqrt09_05    = sqrt( 9.0 /  5.0);
    const float kSqrt05_06    = sqrt( 5.0 /  6.0);
    const float kSqrt01_06    = sqrt( 1.0 /  6.0);
    const float kSqrt03_08    = sqrt( 3.0 /  8.0);
    const float kSqrt05_08    = sqrt( 5.0 /  8.0);
    const float kSqrt07_08    = sqrt( 7.0 /  8.0);
    const float kSqrt09_08    = sqrt( 9.0 /  8.0);
    const float kSqrt05_09    = sqrt( 5.0 /  9.0);
    const float kSqrt08_09    = sqrt( 8.0 /  9.0);

    const float kSqrt01_10    = sqrt( 1.0 / 10.0);
    const float kSqrt03_10    = sqrt( 3.0 / 10.0);
    const float kSqrt01_12    = sqrt( 1.0 / 12.0);
    const float kSqrt04_15    = sqrt( 4.0 / 15.0);
    const float kSqrt01_16    = sqrt( 1.0 / 16.0);
    const float kSqrt07_16    = sqrt( 7.0 / 16.0);
    const float kSqrt15_16    = sqrt(15.0 / 16.0);
    const float kSqrt01_18    = sqrt( 1.0 / 18.0);
    const float kSqrt03_25    = sqrt( 3.0 / 25.0);
    const float kSqrt14_25    = sqrt(14.0 / 25.0);
    const float kSqrt15_25    = sqrt(15.0 / 25.0);
    const float kSqrt18_25    = sqrt(18.0 / 25.0);
    const float kSqrt01_32    = sqrt( 1.0 / 32.0);
    const float kSqrt03_32    = sqrt( 3.0 / 32.0);
    const float kSqrt15_32    = sqrt(15.0 / 32.0);
    const float kSqrt21_32    = sqrt(21.0 / 32.0);
    const float kSqrt01_50    = sqrt( 1.0 / 50.0);
    const float kSqrt03_50    = sqrt( 3.0 / 50.0);
    const float kSqrt21_50    = sqrt(21.0 / 50.0);

    int srcIdx = 0;
    int dstIdx = 0;

    // band 0
    coeffs[dstIdx++] = coeffsIn[0];
    if (n < 2)
        return;

    // band 1
    srcIdx += 1;
    float sh1[3][3] =
    {
        // NOTE: change from upstream code at https://github.com/andrewwillmott/sh-lib, some of the
        // values need to have "-" in front of them.
        orient._22, -orient._23, orient._21,
        -orient._32, orient._33, -orient._31,
        orient._12, -orient._13, orient._11
    };
    coeffs[dstIdx++] = Dot3(srcIdx, coeffsIn, sh1[0]);
    coeffs[dstIdx++] = Dot3(srcIdx, coeffsIn, sh1[1]);
    coeffs[dstIdx++] = Dot3(srcIdx, coeffsIn, sh1[2]);
    if (n < 3)
        return;

    // band 2
    srcIdx += 3;
    float sh2[5][5];

    sh2[0][0] = kSqrt01_04 * ((sh1[2][2] * sh1[0][0] + sh1[2][0] * sh1[0][2]) + (sh1[0][2] * sh1[2][0] + sh1[0][0] * sh1[2][2]));
    sh2[0][1] = (sh1[2][1] * sh1[0][0] + sh1[0][1] * sh1[2][0]);
    sh2[0][2] = kSqrt03_04 * (sh1[2][1] * sh1[0][1] + sh1[0][1] * sh1[2][1]);
    sh2[0][3] = (sh1[2][1] * sh1[0][2] + sh1[0][1] * sh1[2][2]);
    sh2[0][4] = kSqrt01_04 * ((sh1[2][2] * sh1[0][2] - sh1[2][0] * sh1[0][0]) + (sh1[0][2] * sh1[2][2] - sh1[0][0] * sh1[2][0]));

    coeffs[dstIdx++] = Dot5(srcIdx, coeffsIn, sh2[0]);

    sh2[1][0] = kSqrt01_04 * ((sh1[1][2] * sh1[0][0] + sh1[1][0] * sh1[0][2]) + (sh1[0][2] * sh1[1][0] + sh1[0][0] * sh1[1][2]));
    sh2[1][1] = sh1[1][1] * sh1[0][0] + sh1[0][1] * sh1[1][0];
    sh2[1][2] = kSqrt03_04 * (sh1[1][1] * sh1[0][1] + sh1[0][1] * sh1[1][1]);
    sh2[1][3] = sh1[1][1] * sh1[0][2] + sh1[0][1] * sh1[1][2];
    sh2[1][4] = kSqrt01_04 * ((sh1[1][2] * sh1[0][2] - sh1[1][0] * sh1[0][0]) + (sh1[0][2] * sh1[1][2] - sh1[0][0] * sh1[1][0]));

    coeffs[dstIdx++] = Dot5(srcIdx, coeffsIn, sh2[1]);

    sh2[2][0] = kSqrt01_03 * (sh1[1][2] * sh1[1][0] + sh1[1][0] * sh1[1][2]) + -kSqrt01_12 * ((sh1[2][2] * sh1[2][0] + sh1[2][0] * sh1[2][2]) + (sh1[0][2] * sh1[0][0] + sh1[0][0] * sh1[0][2]));
    sh2[2][1] = kSqrt04_03 * sh1[1][1] * sh1[1][0] + -kSqrt01_03 * (sh1[2][1] * sh1[2][0] + sh1[0][1] * sh1[0][0]);
    sh2[2][2] = sh1[1][1] * sh1[1][1] + -kSqrt01_04 * (sh1[2][1] * sh1[2][1] + sh1[0][1] * sh1[0][1]);
    sh2[2][3] = kSqrt04_03 * sh1[1][1] * sh1[1][2] + -kSqrt01_03 * (sh1[2][1] * sh1[2][2] + sh1[0][1] * sh1[0][2]);
    sh2[2][4] = kSqrt01_03 * (sh1[1][2] * sh1[1][2] - sh1[1][0] * sh1[1][0]) + -kSqrt01_12 * ((sh1[2][2] * sh1[2][2] - sh1[2][0] * sh1[2][0]) + (sh1[0][2] * sh1[0][2] - sh1[0][0] * sh1[0][0]));

    coeffs[dstIdx++] = Dot5(srcIdx, coeffsIn, sh2[2]);

    sh2[3][0] = kSqrt01_04 * ((sh1[1][2] * sh1[2][0] + sh1[1][0] * sh1[2][2]) + (sh1[2][2] * sh1[1][0] + sh1[2][0] * sh1[1][2]));
    sh2[3][1] = sh1[1][1] * sh1[2][0] + sh1[2][1] * sh1[1][0];
    sh2[3][2] = kSqrt03_04 * (sh1[1][1] * sh1[2][1] + sh1[2][1] * sh1[1][1]);
    sh2[3][3] = sh1[1][1] * sh1[2][2] + sh1[2][1] * sh1[1][2];
    sh2[3][4] = kSqrt01_04 * ((sh1[1][2] * sh1[2][2] - sh1[1][0] * sh1[2][0]) + (sh1[2][2] * sh1[1][2] - sh1[2][0] * sh1[1][0]));

    coeffs[dstIdx++] = Dot5(srcIdx, coeffsIn, sh2[3]);

    sh2[4][0] = kSqrt01_04 * ((sh1[2][2] * sh1[2][0] + sh1[2][0] * sh1[2][2]) - (sh1[0][2] * sh1[0][0] + sh1[0][0] * sh1[0][2]));
    sh2[4][1] = (sh1[2][1] * sh1[2][0] - sh1[0][1] * sh1[0][0]);
    sh2[4][2] = kSqrt03_04 * (sh1[2][1] * sh1[2][1] - sh1[0][1] * sh1[0][1]);
    sh2[4][3] = (sh1[2][1] * sh1[2][2] - sh1[0][1] * sh1[0][2]);
    sh2[4][4] = kSqrt01_04 * ((sh1[2][2] * sh1[2][2] - sh1[2][0] * sh1[2][0]) - (sh1[0][2] * sh1[0][2] - sh1[0][0] * sh1[0][0]));

    coeffs[dstIdx++] = Dot5(srcIdx, coeffsIn, sh2[4]);

    if (n < 4)
        return;

    // band 3
    srcIdx += 5;
    float sh3[7][7];

    sh3[0][0] = kSqrt01_04 * ((sh1[2][2] * sh2[0][0] + sh1[2][0] * sh2[0][4]) + (sh1[0][2] * sh2[4][0] + sh1[0][0] * sh2[4][4]));
    sh3[0][1] = kSqrt03_02 * (sh1[2][1] * sh2[0][0] + sh1[0][1] * sh2[4][0]);
    sh3[0][2] = kSqrt15_16 * (sh1[2][1] * sh2[0][1] + sh1[0][1] * sh2[4][1]);
    sh3[0][3] = kSqrt05_06 * (sh1[2][1] * sh2[0][2] + sh1[0][1] * sh2[4][2]);
    sh3[0][4] = kSqrt15_16 * (sh1[2][1] * sh2[0][3] + sh1[0][1] * sh2[4][3]);
    sh3[0][5] = kSqrt03_02 * (sh1[2][1] * sh2[0][4] + sh1[0][1] * sh2[4][4]);
    sh3[0][6] = kSqrt01_04 * ((sh1[2][2] * sh2[0][4] - sh1[2][0] * sh2[0][0]) + (sh1[0][2] * sh2[4][4] - sh1[0][0] * sh2[4][0]));

    coeffs[dstIdx++] = Dot7(srcIdx, coeffsIn, sh3[0]);

    sh3[1][0] = kSqrt01_06 * (sh1[1][2] * sh2[0][0] + sh1[1][0] * sh2[0][4]) + kSqrt01_06 * ((sh1[2][2] * sh2[1][0] + sh1[2][0] * sh2[1][4]) + (sh1[0][2] * sh2[3][0] + sh1[0][0] * sh2[3][4]));
    sh3[1][1] = sh1[1][1] * sh2[0][0] + (sh1[2][1] * sh2[1][0] + sh1[0][1] * sh2[3][0]);
    sh3[1][2] = kSqrt05_08 * sh1[1][1] * sh2[0][1] + kSqrt05_08 * (sh1[2][1] * sh2[1][1] + sh1[0][1] * sh2[3][1]);
    sh3[1][3] = kSqrt05_09 * sh1[1][1] * sh2[0][2] + kSqrt05_09 * (sh1[2][1] * sh2[1][2] + sh1[0][1] * sh2[3][2]);
    sh3[1][4] = kSqrt05_08 * sh1[1][1] * sh2[0][3] + kSqrt05_08 * (sh1[2][1] * sh2[1][3] + sh1[0][1] * sh2[3][3]);
    sh3[1][5] = sh1[1][1] * sh2[0][4] + (sh1[2][1] * sh2[1][4] + sh1[0][1] * sh2[3][4]);
    sh3[1][6] = kSqrt01_06 * (sh1[1][2] * sh2[0][4] - sh1[1][0] * sh2[0][0]) + kSqrt01_06 * ((sh1[2][2] * sh2[1][4] - sh1[2][0] * sh2[1][0]) + (sh1[0][2] * sh2[3][4] - sh1[0][0] * sh2[3][0]));

    coeffs[dstIdx++] = Dot7(srcIdx, coeffsIn, sh3[1]);

    sh3[2][0] = kSqrt04_15 * (sh1[1][2] * sh2[1][0] + sh1[1][0] * sh2[1][4]) + kSqrt01_05 * (sh1[0][2] * sh2[2][0] + sh1[0][0] * sh2[2][4]) + -sqrt(1.0 / 60.0) * ((sh1[2][2] * sh2[0][0] + sh1[2][0] * sh2[0][4]) - (sh1[0][2] * sh2[4][0] + sh1[0][0] * sh2[4][4]));
    sh3[2][1] = kSqrt08_05 * sh1[1][1] * sh2[1][0] + kSqrt06_05 * sh1[0][1] * sh2[2][0] + -kSqrt01_10 * (sh1[2][1] * sh2[0][0] - sh1[0][1] * sh2[4][0]);
    sh3[2][2] = sh1[1][1] * sh2[1][1] + kSqrt03_04 * sh1[0][1] * sh2[2][1] + -kSqrt01_16 * (sh1[2][1] * sh2[0][1] - sh1[0][1] * sh2[4][1]);
    sh3[2][3] = kSqrt08_09 * sh1[1][1] * sh2[1][2] + kSqrt02_03 * sh1[0][1] * sh2[2][2] + -kSqrt01_18 * (sh1[2][1] * sh2[0][2] - sh1[0][1] * sh2[4][2]);
    sh3[2][4] = sh1[1][1] * sh2[1][3] + kSqrt03_04 * sh1[0][1] * sh2[2][3] + -kSqrt01_16 * (sh1[2][1] * sh2[0][3] - sh1[0][1] * sh2[4][3]);
    sh3[2][5] = kSqrt08_05 * sh1[1][1] * sh2[1][4] + kSqrt06_05 * sh1[0][1] * sh2[2][4] + -kSqrt01_10 * (sh1[2][1] * sh2[0][4] - sh1[0][1] * sh2[4][4]);
    sh3[2][6] = kSqrt04_15 * (sh1[1][2] * sh2[1][4] - sh1[1][0] * sh2[1][0]) + kSqrt01_05 * (sh1[0][2] * sh2[2][4] - sh1[0][0] * sh2[2][0]) + -sqrt(1.0 / 60.0) * ((sh1[2][2] * sh2[0][4] - sh1[2][0] * sh2[0][0]) - (sh1[0][2] * sh2[4][4] - sh1[0][0] * sh2[4][0]));

    coeffs[dstIdx++] = Dot7(srcIdx, coeffsIn, sh3[2]);

    sh3[3][0] = kSqrt03_10 * (sh1[1][2] * sh2[2][0] + sh1[1][0] * sh2[2][4]) + -kSqrt01_10 * ((sh1[2][2] * sh2[3][0] + sh1[2][0] * sh2[3][4]) + (sh1[0][2] * sh2[1][0] + sh1[0][0] * sh2[1][4]));
    sh3[3][1] = kSqrt09_05 * sh1[1][1] * sh2[2][0] + -kSqrt03_05 * (sh1[2][1] * sh2[3][0] + sh1[0][1] * sh2[1][0]);
    sh3[3][2] = kSqrt09_08 * sh1[1][1] * sh2[2][1] + -kSqrt03_08 * (sh1[2][1] * sh2[3][1] + sh1[0][1] * sh2[1][1]);
    sh3[3][3] = sh1[1][1] * sh2[2][2] + -kSqrt01_03 * (sh1[2][1] * sh2[3][2] + sh1[0][1] * sh2[1][2]);
    sh3[3][4] = kSqrt09_08 * sh1[1][1] * sh2[2][3] + -kSqrt03_08 * (sh1[2][1] * sh2[3][3] + sh1[0][1] * sh2[1][3]);
    sh3[3][5] = kSqrt09_05 * sh1[1][1] * sh2[2][4] + -kSqrt03_05 * (sh1[2][1] * sh2[3][4] + sh1[0][1] * sh2[1][4]);
    sh3[3][6] = kSqrt03_10 * (sh1[1][2] * sh2[2][4] - sh1[1][0] * sh2[2][0]) + -kSqrt01_10 * ((sh1[2][2] * sh2[3][4] - sh1[2][0] * sh2[3][0]) + (sh1[0][2] * sh2[1][4] - sh1[0][0] * sh2[1][0]));

    coeffs[dstIdx++] = Dot7(srcIdx, coeffsIn, sh3[3]);

    sh3[4][0] = kSqrt04_15 * (sh1[1][2] * sh2[3][0] + sh1[1][0] * sh2[3][4]) + kSqrt01_05 * (sh1[2][2] * sh2[2][0] + sh1[2][0] * sh2[2][4]) + -sqrt(1.0 / 60.0) * ((sh1[2][2] * sh2[4][0] + sh1[2][0] * sh2[4][4]) + (sh1[0][2] * sh2[0][0] + sh1[0][0] * sh2[0][4]));
    sh3[4][1] = kSqrt08_05 * sh1[1][1] * sh2[3][0] + kSqrt06_05 * sh1[2][1] * sh2[2][0] + -kSqrt01_10 * (sh1[2][1] * sh2[4][0] + sh1[0][1] * sh2[0][0]);
    sh3[4][2] = sh1[1][1] * sh2[3][1] + kSqrt03_04 * sh1[2][1] * sh2[2][1] + -kSqrt01_16 * (sh1[2][1] * sh2[4][1] + sh1[0][1] * sh2[0][1]);
    sh3[4][3] = kSqrt08_09 * sh1[1][1] * sh2[3][2] + kSqrt02_03 * sh1[2][1] * sh2[2][2] + -kSqrt01_18 * (sh1[2][1] * sh2[4][2] + sh1[0][1] * sh2[0][2]);
    sh3[4][4] = sh1[1][1] * sh2[3][3] + kSqrt03_04 * sh1[2][1] * sh2[2][3] + -kSqrt01_16 * (sh1[2][1] * sh2[4][3] + sh1[0][1] * sh2[0][3]);
    sh3[4][5] = kSqrt08_05 * sh1[1][1] * sh2[3][4] + kSqrt06_05 * sh1[2][1] * sh2[2][4] + -kSqrt01_10 * (sh1[2][1] * sh2[4][4] + sh1[0][1] * sh2[0][4]);
    sh3[4][6] = kSqrt04_15 * (sh1[1][2] * sh2[3][4] - sh1[1][0] * sh2[3][0]) + kSqrt01_05 * (sh1[2][2] * sh2[2][4] - sh1[2][0] * sh2[2][0]) + -sqrt(1.0 / 60.0) * ((sh1[2][2] * sh2[4][4] - sh1[2][0] * sh2[4][0]) + (sh1[0][2] * sh2[0][4] - sh1[0][0] * sh2[0][0]));

    coeffs[dstIdx++] = Dot7(srcIdx, coeffsIn, sh3[4]);

    sh3[5][0] = kSqrt01_06 * (sh1[1][2] * sh2[4][0] + sh1[1][0] * sh2[4][4]) + kSqrt01_06 * ((sh1[2][2] * sh2[3][0] + sh1[2][0] * sh2[3][4]) - (sh1[0][2] * sh2[1][0] + sh1[0][0] * sh2[1][4]));
    sh3[5][1] = sh1[1][1] * sh2[4][0] + (sh1[2][1] * sh2[3][0] - sh1[0][1] * sh2[1][0]);
    sh3[5][2] = kSqrt05_08 * sh1[1][1] * sh2[4][1] + kSqrt05_08 * (sh1[2][1] * sh2[3][1] - sh1[0][1] * sh2[1][1]);
    sh3[5][3] = kSqrt05_09 * sh1[1][1] * sh2[4][2] + kSqrt05_09 * (sh1[2][1] * sh2[3][2] - sh1[0][1] * sh2[1][2]);
    sh3[5][4] = kSqrt05_08 * sh1[1][1] * sh2[4][3] + kSqrt05_08 * (sh1[2][1] * sh2[3][3] - sh1[0][1] * sh2[1][3]);
    sh3[5][5] = sh1[1][1] * sh2[4][4] + (sh1[2][1] * sh2[3][4] - sh1[0][1] * sh2[1][4]);
    sh3[5][6] = kSqrt01_06 * (sh1[1][2] * sh2[4][4] - sh1[1][0] * sh2[4][0]) + kSqrt01_06 * ((sh1[2][2] * sh2[3][4] - sh1[2][0] * sh2[3][0]) - (sh1[0][2] * sh2[1][4] - sh1[0][0] * sh2[1][0]));

    coeffs[dstIdx++] = Dot7(srcIdx, coeffsIn, sh3[5]);

    sh3[6][0] = kSqrt01_04 * ((sh1[2][2] * sh2[4][0] + sh1[2][0] * sh2[4][4]) - (sh1[0][2] * sh2[0][0] + sh1[0][0] * sh2[0][4]));
    sh3[6][1] = kSqrt03_02 * (sh1[2][1] * sh2[4][0] - sh1[0][1] * sh2[0][0]);
    sh3[6][2] = kSqrt15_16 * (sh1[2][1] * sh2[4][1] - sh1[0][1] * sh2[0][1]);
    sh3[6][3] = kSqrt05_06 * (sh1[2][1] * sh2[4][2] - sh1[0][1] * sh2[0][2]);
    sh3[6][4] = kSqrt15_16 * (sh1[2][1] * sh2[4][3] - sh1[0][1] * sh2[0][3]);
    sh3[6][5] = kSqrt03_02 * (sh1[2][1] * sh2[4][4] - sh1[0][1] * sh2[0][4]);
    sh3[6][6] = kSqrt01_04 * ((sh1[2][2] * sh2[4][4] - sh1[2][0] * sh2[4][0]) - (sh1[0][2] * sh2[0][4] - sh1[0][0] * sh2[0][0]));

    coeffs[dstIdx++] = Dot7(srcIdx, coeffsIn, sh3[6]);
}

#endif // SPHERICAL_HARMONICS_HLSL
