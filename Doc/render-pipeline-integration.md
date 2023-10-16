## How does Gaussian Splatting integrate with the rest of rendering?

### How do gaussians interact with regular rendering?

GaussianSplatRenderer objects are rendered after all opaque objects and skybox is rendered, and are tested against Z buffer.
This means you _can_ have opaque objects inside the "gaussian scene", and the splats will be occluded properly.

However this does not work the other way around - the gaussians do _not_ write into the Z buffer, and they are rendered before
all transparencies. So they will not interact with "regular" semitransparent objects well.

### Are gaussians affected by lighting?

No. No lights, shadows, reflection probes, lightmaps, skybox, any of that stuff.

### Rendering order of multiple Gaussian Splat objects

If you have multiple GaussianSplatRenderer objects, they will be _very roughly_ ordered, by their Transform positions.
This means that if GS objects are "sufficiently not overlapping", they will render and composite correctly, but if one of them
is inside another one, or overlapping "a lot", then depending on the viewing direction and their relative ordering, you can
get incorrect rendering results.

This is very much the same issue as you'd have with overlapping particle systems, or overlapping semitransparent objects in "regular"
rendering. It's hard to solve properly!
