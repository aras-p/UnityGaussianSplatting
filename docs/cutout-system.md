# Gaussian Cutout System

This document describes the implementation of the Gaussian Cutout system in this library and provides a plan for extending it with new shapes like Cones.

## 1. How the Cutout System Works

The cutout system is primarily implemented in the GPU shaders to allow real-time filtering of millions of Gaussian splats without modifying the original data on the CPU.

### C# Side (`GaussianCutout.cs`, `GaussianSplatRenderer.cs`)
- **`GaussianCutout` Component**: Defines a cutout volume. It currently supports two types: `Box` and `Ellipsoid`. It also has an `m_Invert` flag.
- **Shader Data**: The cutout's world-to-local transformation matrix and its type/flags are packed into a `ShaderData` struct and sent to the GPU as a `StructuredBuffer<GaussianCutoutShaderData>`.
- **Renderer Integration**: `GaussianSplatRenderer` maintains a list of `GaussianCutout` objects. During the view calculation step (`CalcViewData`), it updates the GPU buffer with the latest transformation matrices of all active cutouts.

### Shader Side (`SplatUtilities.compute`)
The core logic resides in the `IsSplatCut(float3 pos)` function, which is called for every splat during the `CSCalcViewData` pass.

```hlsl
bool IsSplatCut(float3 pos)
{
    bool finalCut = false;
    for (uint i = 0; i < _SplatCutoutsCount; ++i)
    {
        GaussianCutoutShaderData cutData = _SplatCutouts[i];
        uint type = cutData.typeAndFlags & 0xFF;
        bool invert = (cutData.typeAndFlags & 0xFF00) != 0;

        // Transform splat position to cutout's local space
        float3 cutoutPos = mul(cutData.mat, float4(pos, 1)).xyz;

        // Check if inside the unit volume
        bool inside = false;
        if (type == SPLAT_CUTOUT_TYPE_ELLIPSOID)
            inside = dot(cutoutPos, cutoutPos) <= 1;
        else if (type == SPLAT_CUTOUT_TYPE_BOX)
            inside = all(abs(cutoutPos) <= 1);

        // Early return if inside:
        // - If normal (invert=false) and inside, it's NOT cut.
        // - If inverted (invert=true) and inside, it IS cut.
        if (inside) return invert;

        // If outside, keep track if we should cut it by default (for normal cutouts)
        finalCut |= !invert;
    }
    return finalCut;
}
```

**Logical Behavior:**
- **Normal Cutout**: Acts as a "Volume of Interest". Splats are hidden unless they are inside at least one normal cutout.
- **Inverted Cutout**: Acts as a "Deletion Volume". Splats are hidden if they are inside any inverted cutout.
- **Precedence**: If a splat is inside multiple cutouts, the one appearing first in the `_SplatCutouts` list determines the result.

---

## 2. Extending the System: Cone and Elliptic Cone

To support Cone and Elliptic Cone cutouts, the following changes are proposed.

### Defining a Unit Cone
We define a "unit cone" in local space that can be scaled and rotated via the Cutout's Transform.
- **Apex**: `(0, 0, 0)` (The Pivot)
- **Base**: `y = 1`
- **Base Radius**: `1`
- **Axis**: Positive Y-axis.

**Shader implementation of `inside` check:**
```hlsl
if (type == SPLAT_CUTOUT_TYPE_CONE)
{
    // Height h = 1 (from 0 to 1)
    // Radius at height y: r = y
    bool withinHeight = cutoutPos.y >= 0 && cutoutPos.y <= 1;
    float r = cutoutPos.y;
    inside = withinHeight && (dot(cutoutPos.xz, cutoutPos.xz) <= r * r);
}
```

### Elliptic Cone
An "Elliptic Cone" does not require a new type. Since the cutout logic operates in local space after applying the `worldToLocalMatrix`, any non-uniform scaling on the Transform will automatically result in an elliptical cross-section.
- To create an elliptic cone, simply scale the `GaussianCutout` GameObject differently along the X and Z axes.

### Application: Camera Direction Cutout
To "pluck out" a certain direction seen from the camera:
1. Attach a `GaussianCutout` (type: Cone) to the Camera or place it at the camera position.
2. Align the rotation so the Cone's local Y axis points in the camera's forward direction (rotate Cone 90 degrees around X).
3. Adjust the scale:
   - **Scale Y**: Controls the **length** (far clip distance) of the cutout. Changing Y does **not** affect the Field of View (FOV).
   - **Scale X / Z**: Controls the **FOV** (opening angle). X and Z can be different to create an elliptical FOV.
4. If `Invert` is off, only splats within this "flashlight" volume will be visible.

## Proposed Code Changes

### `GaussianCutout.cs`
- Add `Cone` to the `Type` enum.
- Update `OnDrawGizmos` to draw a cone wireframe (using a helper or simple lines).

### `SplatUtilities.compute`
- Define `SPLAT_CUTOUT_TYPE_CONE 2`.
- Update `IsSplatCut` to handle `SPLAT_CUTOUT_TYPE_CONE`.

### `GaussianSplatAssetEditor.cs` (Optional)
- Ensure the UI correctly displays the new type (Enum should handle this automatically).
