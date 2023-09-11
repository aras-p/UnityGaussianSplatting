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
}
