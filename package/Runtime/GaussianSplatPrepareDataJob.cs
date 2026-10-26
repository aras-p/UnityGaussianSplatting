// SPDX-License-Identifier: MIT
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace GaussianSplatting.Runtime
{
    // Converts input data (PLY vertices or decomprssed SPZ attribute streams) into
    // our GPU layout. Each iteration converts a contiguous batch and calculated bounds of it.
    [BurstCompile(CompileSynchronously = true)]
    unsafe struct GaussianSplatPrepareDataJob : IJobParallelFor
    {
        public const int kBatchSize = 1024;
        public struct BatchResult { public float3 min, max; public int invalidSplat; }

        struct InputSplatData
        {
            public float3 position, scale, color;
            public float4 rotation;
            public uint packedRotation; // Original SPZ v3 word, preserved without requantization.
            public float opacity;
        }

        [ReadOnly] public NativeArray<byte> input;
        public int stride, count, outputStart, shCoefficients;

        // PLY property byte offsets: position XYZ, scale XYZ, quaternion XYZW,
        // color RGB, opacity, then channel-major SH.
        [ReadOnly] public NativeArray<int> plyPropOffsets;

        // spzVersion == 0 selects PLY; versions 1–3 select SPZ.
        public int spzVersion;
        public int spzTotalCount, spzFractionalBits;

        [NativeDisableParallelForRestriction] public NativeArray<uint> positions, geometry, colors, sh;
        [WriteOnly] public NativeArray<BatchResult> results;

        public void Execute(int batch)
        {
            byte* src = (byte*)input.GetUnsafeReadOnlyPtr();
            float3* sh = stackalloc float3[15];
            var result = new BatchResult { min = new float3(float.PositiveInfinity), max = new float3(float.NegativeInfinity) };
            int end = math.min(count, (batch + 1) * kBatchSize);
            for (int local = batch * kBatchSize; local < end; ++local)
            {
                int index = outputStart + local;
                
                // decode input data for one splat
                InputSplatData splat = spzVersion == 0
                    ? DecodePly(src + local * stride, sh)
                    : DecodeSpz(src, index, sh);

                // encode splat into our GPU format
                bool valid =
                    math.all(math.isfinite(splat.position)) &&
                    FitsHalf(splat.scale) &&
                    FitsHalf(splat.color) &&
                    math.isfinite(splat.opacity) &&
                    (spzVersion >= 3 || (math.all(math.isfinite(splat.rotation)) &&
                        math.lengthsq(splat.rotation) >= 1e-20f));
                float range = 0;
                for (int c = 0; c < shCoefficients; ++c)
                {
                    valid &= FitsHalf(sh[c]);
                    range = math.max(range, math.cmax(math.abs(sh[c])));
                }
                if (!valid)
                {
                    result.invalidSplat = index + 1;
                    break;
                }
                result.min = math.min(result.min, splat.position);
                result.max = math.max(result.max, splat.position);
                positions[index * 3 + 0] = math.asuint(splat.position.x);
                positions[index * 3 + 1] = math.asuint(splat.position.y);
                positions[index * 3 + 2] = math.asuint(splat.position.z);
                geometry[index * 3 + 0] = spzVersion >= 3
                    ? splat.packedRotation
                    : GaussianUtils.EncodeSpzSmallest3Rotation(math.normalize(splat.rotation));
                geometry[index * 3 + 1] = Half2(splat.scale.x, splat.scale.y);
                geometry[index * 3 + 2] = Half2(splat.scale.z, 0);
                colors[index * 2 + 0] = Half2(splat.color.x, splat.color.y);
                colors[index * 2 + 1] = Half2(splat.color.z, splat.opacity);
                if (shCoefficients > 0)
                {
                    int offset = index * (shCoefficients + 1);
                    this.sh[offset] = math.asuint(range);
                    for (int c = 0; c < shCoefficients; ++c)
                        this.sh[offset + c + 1] = GaussianUtils.PackSHCoefficient(sh[c], range);
                }
            }
            results[batch] = result;
        }

        // PLY stores just float properties; some of them encoded in non-linear way.
        InputSplatData DecodePly(byte* row, float3* coefficients)
        {
            InputSplatData splat;
            splat.packedRotation = 0;
            splat.position = new float3(PlyFloat(row, 0), PlyFloat(row, 1), PlyFloat(row, 2));
            // scale is log encoded
            splat.scale = math.exp(new float3(PlyFloat(row, 3), PlyFloat(row, 4), PlyFloat(row, 5)));
            splat.rotation = new float4(PlyFloat(row, 6), PlyFloat(row, 7), PlyFloat(row, 8), PlyFloat(row, 9));
            splat.color = GaussianUtils.SH0ToColor(new float3(PlyFloat(row, 10), PlyFloat(row, 11), PlyFloat(row, 12)));
            // opacity is logit encoded
            float logit = PlyFloat(row, 13);
            splat.opacity = math.isfinite(logit) ? GaussianUtils.Sigmoid(logit) : float.NaN;
            for (int c = 0; c < shCoefficients; ++c)
                coefficients[c] = new float3(PlyFloat(row, 14 + c), PlyFloat(row, 14 + c + shCoefficients), PlyFloat(row, 14 + c + shCoefficients * 2));
            return splat;
        }

        // SPZ stores separate packed streams: positions, alpha, color, scale, rotation, SH.
        // The data has already been decompressed.
        InputSplatData DecodeSpz(byte* src, int index, float3* coefficients)
        {
            InputSplatData splat;
            splat.packedRotation = 0;

            int positionBytes = spzVersion == 1 ? 6 : 9;
            byte* p = src + index * positionBytes;
            float fraction = math.rcp(1 << spzFractionalBits);
            splat.position = spzVersion == 1
                ? new float3(math.f16tof32(*(ushort*)p), math.f16tof32(*(ushort*)(p + 2)), math.f16tof32(*(ushort*)(p + 4)))
                : new float3(Fixed24(p, fraction), Fixed24(p + 3, fraction), Fixed24(p + 6, fraction));
            byte* alpha = src + spzTotalCount * positionBytes;
            splat.opacity = alpha[index] / 255f;
            byte* col = alpha + spzTotalCount + index * 3;
            splat.color = GaussianUtils.SH0ToColor((new float3(col[0], col[1], col[2]) / 255f - 0.5f) / 0.15f);
            byte* scl = alpha + spzTotalCount * 4 + index * 3;
            splat.scale = math.exp(new float3(scl[0], scl[1], scl[2]) / 16f - 10f);
            int rotationBytes = spzVersion < 3 ? 3 : 4;
            byte* rot = alpha + spzTotalCount * 7 + index * rotationBytes;
            splat.rotation = 0;
            if (spzVersion < 3)
            {
                splat.rotation.xyz = new float3(rot[0], rot[1], rot[2]) / 127.5f - 1;
                splat.rotation.w = math.sqrt(math.max(0, 1 - math.lengthsq(splat.rotation.xyz)));
            }
            else
            {
                splat.packedRotation = *(uint*)rot;
            }
            byte* harmonics = alpha + spzTotalCount * (7 + rotationBytes) + index * shCoefficients * 3;
            for (int c = 0; c < shCoefficients; ++c)
                coefficients[c] = (new float3(harmonics[c * 3], harmonics[c * 3 + 1], harmonics[c * 3 + 2]) - 128) / 128f;
            return splat;
        }

        static uint Half2(float a, float b) => math.f32tof16(a) | (math.f32tof16(b) << 16);
        static bool FitsHalf(float3 v) => math.all(math.isfinite(v)) && math.cmax(math.abs(v)) <= 65504;

        float PlyFloat(byte* row, int prop)
        {
            return *(float*)(row + plyPropOffsets[prop]);
        }
        static float Fixed24(byte* p, float scale)
        {
            int v = p[0] | (p[1] << 8) | (p[2] << 16);
            return ((v << 8) >> 8) * scale;
        }
    }
}
