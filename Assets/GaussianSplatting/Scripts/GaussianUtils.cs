using Unity.Mathematics;

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

    public static float3 LinearScale(float3 logScale)
    {
        return math.abs(math.exp(logScale));
    }

    public static float4 NormalizeSwizzleRotation(float4 wxyz)
    {
        return math.normalize(wxyz).yzwx;
    }

    // Based on https://fgiesen.wordpress.com/2009/12/13/decoding-morton-codes/
    // Insert two 0 bits after each of the 21 low bits of x
    static ulong MortonPart1By2(ulong x)
    {
        x &= 0x1fffff;
        x = (x ^ (x << 32)) & 0x1f00000000ffffUL;
        x = (x ^ (x << 16)) & 0x1f0000ff0000ffUL;
        x = (x ^ (x << 8)) & 0x100f00f00f00f00fUL;
        x = (x ^ (x << 4)) & 0x10c30c30c30c30c3UL;
        x = (x ^ (x << 2)) & 0x1249249249249249UL;
        return x;
    }
    // Encode three 21-bit integers into 3D Morton order
    public static ulong MortonEncode3(uint3 v)
    {
        return (MortonPart1By2(v.z) << 2) | (MortonPart1By2(v.y) << 1) | MortonPart1By2(v.x);
    }
}
