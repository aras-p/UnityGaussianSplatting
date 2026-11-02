// SPDX-License-Identifier: MIT
using System;
using System.IO;
using Unity.Collections;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    // CPU side buffers for uploading the splat data to the GPU.
    // Disposed after the upload.
    sealed class GaussianSplatData : IDisposable
    {
        public readonly int count, shDegree, shCoefficients;
        public NativeArray<uint> positions, geometry, colors, sh;
        public Bounds bounds;

        public GaussianSplatData(int count, int degree)
        {
            if (count <= 0 || count > 16_000_000)
                throw new InvalidDataException("Splat count must be between 1 and 16,000,000");
            if (degree < 0 || degree > 3)
                throw new InvalidDataException("Only SH degrees 0 through 3 are supported");
            this.count = count;
            shDegree = degree;
            shCoefficients = (degree + 1) * (degree + 1) - 1;
            try
            {
                positions = Allocate(count * 3); // float3
                geometry = Allocate(count * 3); // quat, half2 scaleXY, half2 scaleZ/opacity
                colors = Allocate(count); // biased RGB10E2
                sh = Allocate(shCoefficients == 0 ? 1 : count * (GaussianUtils.GetSHDataSize(shCoefficients) / 4));
                if (shCoefficients == 0) sh[0] = 0;
            }
            catch { Dispose(); throw; }
        }

        static NativeArray<uint> Allocate(int length) => new(length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

        public void Dispose()
        {
            if (positions.IsCreated) positions.Dispose();
            if (geometry.IsCreated) geometry.Dispose();
            if (colors.IsCreated) colors.Dispose();
            if (sh.IsCreated) sh.Dispose();
        }
    }
}
