// SPDX-License-Identifier: MIT

using Unity.Mathematics;

namespace GaussianSplatting.Runtime
{
    public static class GaussianUtils
    {
        public static float Sigmoid(float v)
        {
            return math.rcp(1.0f + math.exp(-v));
        }

        public static float3 SH0ToColor(float3 dc0)
        {
            const float kSH_C0 = 0.2820948f;
            return dc0 * kSH_C0 + 0.5f;
        }

        // Encode a normalized XYZW quaternion using SPZ v3's smallest-three format.
        public static uint EncodeSpzSmallest3Rotation(float4 rotation)
        {
            float4 absRotation = math.abs(rotation);
            int largest = 0;
            for (int c = 1; c < 4; ++c)
                if (absRotation[c] > absRotation[largest]) largest = c;
            if (rotation[largest] < 0) rotation = -rotation;
            uint packed = (uint)largest;
            for (int c = 0; c < 4; ++c)
            {
                if (c == largest) continue;
                uint magnitude = (uint)(511f * (math.abs(rotation[c]) / 0.7071067811865475f) + 0.5f);
                uint sign = rotation[c] < 0 ? 512u : 0u;
                packed = (packed << 10) | sign | magnitude;
            }
            return packed;
        }

        // Pack one RGB SH coefficient into signed 11.10.11 bits, using the per-splat maximum absolute value
        public static uint PackSHCoefficient(float3 sh, float range)
        {
            int3 v = (int3)math.round((range > 0 ? sh / range : 0) * new float3(1023, 511, 1023));
            return ((uint)v.x & 2047) | (((uint)v.y & 1023) << 11) | (((uint)v.z & 2047) << 21);
        }
    }
}
