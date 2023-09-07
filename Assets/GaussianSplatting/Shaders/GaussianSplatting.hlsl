#ifndef GAUSSIAN_SPLATTING_HLSL
#define GAUSSIAN_SPLATTING_HLSL

float3 QuatRotateVector(float3 v, float4 r)
{
    float3 t = 2 * cross(r.xyz, v);
    return v + r.w * t + cross(r.xyz, t);
}

float4 QuatInverse(float4 q)
{
    return rcp(dot(q, q)) * q * float4(-1,-1,-1,1);
}

float Sigmoid(float v)
{
    return rcp(1.0 + exp(-v));
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
float3 CalcCovariance2D(float3 worldPos, float3 cov3d0, float3 cov3d1)
{
    float4x4 viewMatrix = UNITY_MATRIX_V;
    float3 viewPos = mul(viewMatrix, float4(worldPos, 1)).xyz;

    // this is needed in order for splats that are visible in view but clipped "quite a lot" to work
    float aspect = UNITY_MATRIX_P._m00 / UNITY_MATRIX_P._m11;
    float tanFovX = rcp(UNITY_MATRIX_P._m00);
    float tanFovY = rcp(UNITY_MATRIX_P._m11 * aspect);
    float limX = 1.3 * tanFovX;
    float limY = 1.3 * tanFovY;
    viewPos.x = clamp(viewPos.x / viewPos.z, -limX, limX) * viewPos.z;
    viewPos.y = clamp(viewPos.y / viewPos.z, -limY, limY) * viewPos.z;

    float focal = _ScreenParams.x * UNITY_MATRIX_P._m00 / 2;

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

float2 CalcScreenSpaceDelta(float2 svPositionXY, float2 centerXY)
{
    float2 d = svPositionXY - centerXY;
    d.y *= _ProjectionParams.x;
    return d;
}

float CalcPowerFromConic(float3 conic, float2 d)
{
    return -0.5 * (conic.x * d.x*d.x + conic.z * d.y*d.y) + conic.y * d.x*d.y;
}


#endif // GAUSSIAN_SPLATTING_HLSL
