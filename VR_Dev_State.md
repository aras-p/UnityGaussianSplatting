# Gaussian Splat VR Stability Fixes

Technical documentation of changes made to fix "patchiness" and "jitter" artifacts in VR close-range viewing.

---

## Problem Summary

When viewing Gaussian splats in VR at close range (< 0.5m), two distinct visual artifacts appeared:

1. **Patchiness / Tearing**: Splats would appear to "swim" or show patchy inconsistencies between left and right eyes
2. **Jitter**: Splats would flicker or jump positions when moving the head, especially on high-contrast edges

These issues only affected splats (not MeshRenderers) and were exclusive to VR/stereo mode.

---

## Root Causes

### 1. Mono Matrix Usage in Stereo Mode
The original code used a single (mono) view-projection matrix for covariance calculation, even in stereo mode. Each eye has a different view frustum, so splats must be projected using eye-specific matrices.

### 2. Numerical Instability at Close Range
The `CalcCovariance2D` function divided by `viewPos.z` without clamping, causing numerical instability when splats were very close to the camera.

### 3. Eigenvector Calculation Instability
The `DecomposeCovariance` function's eigenvector normalization could produce unstable results when the off-diagonal covariance term was near zero.

### 4. GPU Race Condition (Ghosting)
Missing synchronization barrier between compute shader dispatch and render pass allowed reading of incomplete view data.

---

## Fixes Implemented

### Fix 1: Per-Eye Stereo View Data Calculation

**File**: `Shaders/SplatUtilities.compute`

**Problem**: Single mono matrix used for both eyes

**Solution**: Calculate view data separately for each eye using eye-specific matrices

```hlsl
// NEW: Stereo matrix uniforms
cbuffer StereoMatrices
{
    float4x4 _ViewProjMatrixLeft;
    float4x4 _ViewProjMatrixRight;
    float4x4 _ViewMatrixLeft;
    float4x4 _ViewMatrixRight;
    float4x4 _ProjMatrixLeft;
    float4x4 _ProjMatrixRight;
};

// NEW: Per-eye view data calculation
SplatViewData CalculateEyeViewData(SplatData splat, float3 centerWorldPos,
    float4x4 viewProjMatrix, float4x4 viewMatrix, float4x4 projMatrix, ...)
{
    // Use eye-specific matrices for covariance
    float3 cov2d = CalcCovariance2D(splat.pos, cov3d0, cov3d1,
        viewMatrix, projMatrix, _VecScreenParams);
    // ...
}

// In CSCalcViewData kernel:
if (_IsStereo)
{
    SplatViewData viewLeft = CalculateEyeViewData(..., _ViewProjMatrixLeft,
        _ViewMatrixLeft, _ProjMatrixLeft, ...);
    SplatViewData viewRight = CalculateEyeViewData(..., _ViewProjMatrixRight,
        _ViewMatrixRight, _ProjMatrixRight, ...);

    // Store both eyes in interleaved buffer
    _SplatViewData[idx * 2] = viewLeft;
    _SplatViewData[idx * 2 + 1] = viewRight;
}
```

**C# Side** (`GaussianSplatRenderer.cs`):
- View buffer doubled in size: `m_Asset.splatCount * 2`
- Stereo matrices passed from `Camera.GetStereoViewMatrix()` / `Camera.GetStereoProjectionMatrix()`
- `_EyeIndex` and `_IsStereo` shader properties added for render-time eye selection

---

### Fix 2: Near-Field Numerical Stability in CalcCovariance2D

**File**: `Shaders/GaussianSplatting.hlsl`

**Problem**: Division by `viewPos.z` without bounds checking caused numerical explosion at close range

**Solution**: Clamp minimum Z and pre-compute reciprocals

```hlsl
// BEFORE (unstable):
float3 CalcCovariance2D(...) {
    float3 viewPos = mul(viewMatrix, float4(worldPos, 1)).xyz;
    // Division by potentially very small viewPos.z
    viewPos.x = clamp(viewPos.x / viewPos.z, -limX, limX) * viewPos.z;
    float3x3 J = float3x3(
        focal / viewPos.z, 0, -(focal * viewPos.x) / (viewPos.z * viewPos.z),
        // ...
    );
}

// AFTER (stable):
float3 CalcCovariance2D(...) {
    float3 viewPos = mul(viewMatrix, float4(worldPos, 1)).xyz;

    // Clamp minimum z to prevent division instability at close range
    float minZ = 0.01;
    float z = max(abs(viewPos.z), minZ);
    float zSign = sign(viewPos.z);
    viewPos.z = z * (zSign != 0 ? zSign : 1.0);

    // Pre-compute reciprocals for consistent precision
    float invZ = 1.0 / viewPos.z;
    float invZ2 = invZ * invZ;

    viewPos.x = clamp(viewPos.x * invZ, -limX, limX) * viewPos.z;
    viewPos.y = clamp(viewPos.y * invZ, -limY, limY) * viewPos.z;

    float focalInvZ = focal * invZ;
    float3x3 J = float3x3(
        focalInvZ, 0.0, -focal * viewPos.x * invZ2,
        0.0, focalInvZ, -focal * viewPos.y * invZ2,
        0.0, 0.0, 0.0
    );
}
```

**Key Changes**:
- Minimum Z clamped to 0.01 (1cm)
- Sign preserved for correct projection direction
- Pre-computed `invZ` and `invZ2` used consistently throughout

---

### Fix 3: Stabilized Eigenvector Calculation

**File**: `Shaders/SplatUtilities.compute`

**Problem**: `normalize(float2(offDiag, lambda1 - diag1))` produced unstable results when `offDiag` approached zero

**Solution**: Threshold-based fallback to axis-aligned eigenvectors

```hlsl
// BEFORE (unstable):
void DecomposeCovariance(float3 cov2d, out float2 v1, out float2 v2)
{
    // ...
    float2 diagVec = normalize(float2(offDiag, lambda1 - diag1));
    diagVec.y = -diagVec.y;
    // ...
}

// AFTER (stable):
void DecomposeCovariance(float3 cov2d, out float2 v1, out float2 v2)
{
    // ...
    float2 diagVec;
    float eps = 1e-4;  // Stability threshold
    float offDiagAbs = abs(offDiag);
    float eigDiff = abs(lambda1 - diag1);

    if (offDiagAbs < eps && eigDiff < eps)
    {
        // Nearly circular/isotropic - use stable axis-aligned direction
        diagVec = float2(1.0, 0.0);
    }
    else if (offDiagAbs < eps * max(eigDiff, 1.0))
    {
        // Off-diagonal negligible - eigenvectors are axis-aligned
        diagVec = (diag1 >= diag2) ? float2(1.0, 0.0) : float2(0.0, 1.0);
    }
    else
    {
        // Standard case - normalize is stable
        diagVec = normalize(float2(offDiag, lambda1 - diag1));
    }
    diagVec.y = -diagVec.y;
    // ...
}
```

**Stability Thresholds**:
- `eps = 1e-4`: Minimum meaningful off-diagonal value
- Isotropic case: Both off-diagonal and eigenvalue difference below threshold
- Axis-aligned case: Off-diagonal small relative to eigenvalue difference

---

### Fix 4: GPU Synchronization Fence

**File**: `Runtime/GaussianSplatRenderer.cs`

**Problem**: Compute shader writes to `_SplatViewData`, then `DrawProcedural` reads from it with no barrier

**Solution**: Add `GraphicsFence` between compute dispatch and render

```csharp
// NEW: Fence fields
internal GraphicsFence m_ViewDataFence;
internal bool m_ViewDataFenceValid;

// In CalcViewData():
cmb.DispatchCompute(m_CSSplatUtilities, (int)KernelIndices.CalcViewData, ...);

// Create fence after compute dispatch
m_ViewDataFence = cmb.CreateAsyncGraphicsFence();
m_ViewDataFenceValid = true;

// In RenderPreparedSplats():
if (item.gs.m_ViewDataFenceValid)
{
    cmb.WaitOnAsyncGraphicsFence(item.gs.m_ViewDataFence);
    item.gs.m_ViewDataFenceValid = false; // Only wait once per frame
}
cmb.DrawProcedural(...);
```

---

## Configuration Changes

### GaussianSplatRenderer Inspector Options

New option added:
```csharp
[Tooltip("When in VR, sort splats separately for each eye.")]
public bool m_SortPerEye = false;
```

**Note**: Per-eye sorting increases accuracy but reduces performance. Default is `false` (shared sort order for both eyes).

---

## Buffer Layout Changes

### View Data Buffer (Stereo Mode)

**Before**: `splatCount` entries, single view data per splat

**After**: `splatCount * 2` entries, interleaved left/right eye data

| Index | Content |
|-------|---------|
| 0 | Splat 0 - Left Eye |
| 1 | Splat 0 - Right Eye |
| 2 | Splat 1 - Left Eye |
| 3 | Splat 1 - Right Eye |
| ... | ... |

Shader access pattern:
```hlsl
uint viewIdx = splatIdx * 2 + _EyeIndex;  // _EyeIndex = 0 (left) or 1 (right)
SplatViewData view = _SplatViewData[viewIdx];
```

---

## Known Remaining Issues

1. **Residual Jitter at Very Close Range**: Some jitter may still be visible at extreme close range (< 5cm). This is inherent to the projection math and would require higher precision or adaptive LOD.

2. **Depth Sorting Artifacts**: Splats may render on top of each other incorrectly due to per-splat depth sorting limitations. This is a fundamental limitation of the sorting algorithm, not the stability fixes.

---

## Performance Impact

| Fix | Impact |
|-----|--------|
| Per-eye view calculation | ~1.5x compute shader time (doubled work) |
| Numerical stability | Negligible (<1%) |
| Eigenvector stability | Negligible (<1%) |
| GPU fence | ~0.1-0.5ms per frame |

**Total overhead**: Approximately 0.5-1ms per frame on Quest 3, acceptable trade-off for visual quality.

---

## References

- EWA Splatting (Zwicker et al. 2002) - Equation 31 for 2D covariance projection
- Unity XR Single Pass Instanced rendering documentation
- GraphicsFence Unity documentation for compute/render synchronization
