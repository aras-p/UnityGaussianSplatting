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

        // RGB10E2 with a 0.3125 bias: decoded range [-0.3125, 3.68359375].
        // Non-finite colors are rejected before encoding.
        public static uint PackColorRGB10E2(float3 color)
        {
            color = math.clamp(color + 0.3125f, 0, 3.99609375f);
            float maxColor = math.cmax(color);
            int exponent = math.max(0, (int)(math.asuint(maxColor) >> 23) - 125);
            float scale = math.asfloat((uint)(exponent + 116) << 23); // 2^(exponent - 11)
            // rounding the largest mantissa can carry into the shared exponent.
            if (math.floor(maxColor / scale + 0.5f) >= 1024)
            {
                ++exponent;
                scale *= 2;
            }
            uint3 mantissa = (uint3)math.floor(color / scale + 0.5f);
            return mantissa.x | (mantissa.y << 10) | (mantissa.z << 20) | ((uint)exponent << 30);
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

        // FP16 range followed by RGB565 coefficients, padded to a whole uint
        public static int GetSHDataSize(int coefficients) => coefficients == 0 ? 0 : ((2 + coefficients * 2 + 3) / 4) * 4;

        // Pack one RGB SH coefficient into signed 5.6.5 bits, using per-splat range.
        // Symmetric signed quantization preserves zero exactly.
        public static uint PackSHCoefficient(float3 sh, float range)
        {
            float3 normalized = math.clamp(range > 0 ? sh / range : 0, -1, 1);
            int3 v = (int3)math.round(normalized * new float3(15, 31, 15));
            return ((uint)v.x & 31) | (((uint)v.y & 63) << 5) | (((uint)v.z & 31) << 11);
        }
    }
}
