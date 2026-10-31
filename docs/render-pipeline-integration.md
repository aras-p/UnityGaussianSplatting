## How does Gaussian Splatting integrate with the rest of rendering?

### Which Unity render pipelines are supported?

"All of them" is a simple answer. URP and HDRP require Unity 6 or later. More details:
- I am mostly developing and testing with the built-in render pipeline (BiRP). This one does not need any extra setup;
  just have `GaussianSplatRenderer` components.
- URP: add `GaussianSplatURPFeature` to the URP renderer settings.
  - Render Graph "Compatibility Mode" in URP settings must be turned off!
  - For XR, use OpenXR's **Multi-Pass** render mode!
- HDRP: add CustomPass volume object and a `GaussianSplatHDRPPass` entry to it. It can be setup to render before transparencies,
  or after postprocess. Doing it after postprocess often produces better results, since before transparencies does not play well
  with HDRP auto-exposure thingamabobs.

Note that the project requires DX12 or Vulkan on Windows, i.e. **DX11 will not work**. Go to player settings graphics APIs
section and change Windows to use DX12.

### How do gaussians interact with regular rendering?

GaussianSplatRenderer objects are rendered after all opaque objects and skybox is rendered, and are tested against Z buffer.
This means you _can_ have opaque objects inside the "gaussian scene", and the splats will be occluded properly.

However gaussians will not interact quite properly with regular semitransparent 3D objects in the scene.

### Are gaussians affected by lighting?

No. No lights, shadows, reflection probes, lightmaps, skybox, any of that stuff.

### Known issues where things do not work

- Any kind of MSAA anti-aliasing usage does not work.
