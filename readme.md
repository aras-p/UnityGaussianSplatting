# Toy Gaussian Splatting playground in Unity

SIGGRAPH 2023 had a paper "[3D Gaussian Splatting for Real-Time Radiance Field Rendering](https://repo-sam.inria.fr/fungraph/3d-gaussian-splatting/)" by Kerbl, Kopanas, Leimkühler, Drettakis that looks pretty cool!
Check out their website, source code repository, data sets and so on.

I've decided to try to implement the realtime visualization part (i.e. the one that takes already-produced gaussian splat "model" file) in Unity. The original paper code has a purely CUDA-based realtime renderer; other
people have done WebGPU based visualizers and so on (e.g. [cvlab-epfl/gaussian-splatting-web](https://github.com/cvlab-epfl/gaussian-splatting-web)).

Code in here so far is randomly cribbled together from reading the paper (as well as earlier literature on EWA splatting), looking at the official CUDA implementation, and so on. Current state:
- The code does **not** use the "tile-based splat rasterizer" bit from the paper; it just draws each gaussian splat as a screenspace aligned rectangle that covers the extents of it.
- Splat color accumulation is done by rendering front-to-back, with a blending mode that results in the same accumulated color as their tile-based renderer.
- Splat sorting is done with a simple GPU bitonic sort that is mostly lifted from Unity HDRP codebase.

Both the sorting and the "just draw each splat as a particle" are simple to implement but are not very efficient.

## Usage

- Within Unity, there's a `Scene.unity` that has a `GaussianSplatRenderer` script attached to it.
- You need to point it to a "model" directory (the paper [github page](https://github.com/graphdeco-inria/gaussian-splatting) has a "pre-trained models" link to some). The model directory is expected to contain `cameras.json` and
  `point_cloud/iteration_7000/point_cloud.ply` inside of it.
- Press play.

:warning: Note: this is all _**a toy**_, it is not robust, it does not handle errors gracefully, it does not interact or composite well with the "rest of rendering", it is not fast, etc. etc. I told you so!
