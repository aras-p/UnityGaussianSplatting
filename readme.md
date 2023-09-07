# Toy Gaussian Splatting playground in Unity

SIGGRAPH 2023 had a paper "[**3D Gaussian Splatting for Real-Time Radiance Field Rendering**](https://repo-sam.inria.fr/fungraph/3d-gaussian-splatting/)" by Kerbl, Kopanas, Leimkühler, Drettakis that looks pretty cool!
Check out their website, source code repository, data sets and so on.

I've decided to try to implement the realtime visualization part (i.e. the one that takes already-produced gaussian splat "model" file) in Unity.

![Screenshot](/Screenshot01.png?raw=true "Screenshot")

The original paper code has a purely CUDA-based realtime renderer; other
people have done their own implementations (e.g. WebGPU at [cvlab-epfl](https://github.com/cvlab-epfl/gaussian-splatting-web), Taichi at [wanmeihuali](https://github.com/wanmeihuali/taichi_3d_gaussian_splatting), etc.).

Code in here so far is randomly cribbled together from reading the paper (as well as earlier literature on EWA splatting), looking at the official CUDA implementation, and so on. Current state:
- The code does **not** use the "tile-based splat rasterizer" bit from the paper; it just draws each gaussian splat as a screenspace aligned rectangle that covers the extents of it.
- Splat color accumulation is done by rendering front-to-back, with a blending mode that results in the same accumulated color as their tile-based renderer.
- Splat sorting is done with a simple GPU bitonic sort that is mostly lifted from Unity HDRP codebase.

Both the sorting and the "just draw each splat as a particle" are simple to implement but are not very efficient.

## Usage

- Within Unity (2022.3), there's a `Scene.unity` that has a `GaussianSplatRenderer` script attached to it.
- You need to point it to a "model" directory. The model directory is expected to contain `cameras.json` and
  `point_cloud/iteration_7000/point_cloud.ply` inside of it.
  - Since the models are quite large, I have not included any in this Github repo. The original [paper github page](https://github.com/graphdeco-inria/gaussian-splatting) has a a link to
    [14GB zip](https://repo-sam.inria.fr/fungraph/3d-gaussian-splatting/datasets/pretrained/models.zip) of their models.
- Press play. The gaussian splat renderer component inspector will have a slider to move the game view camera into one of the cameras from the model directory. Or you can just move the game/scene view camera
  however you please.

:warning: Note: this is all _**a toy**_, it is not robust, it does not handle errors gracefully, it does not interact or composite well with the "rest of rendering", it is not fast, etc. etc. I told you so!

Wishlist that I may or might not do at some point:
- Look at ways to make the data sets smaller (both on-disk and in-memory),
- Make sorting faster,
- Make rendering faster (actual tiled compute shader rasterizer),
- Integrate better with "the rest" of rendering that might be in the scene.

## Write-ups

My own blog posts about all this _(so far... not that many!)_:
* [Gaussian Splatting is pretty cool!](https://aras-p.info/blog/2023/09/05/Gaussian-Splatting-is-pretty-cool/) (2023 Sep)

## External Code Used

- [zanders3/json](https://github.com/zanders3/json), MIT license, (c) 2018 Alex Parker.
- "Island" GPU sorting code adapted from [Tim Gfrerer blog post](https://poniesandlight.co.uk/reflect/bitonic_merge_sort/).
- "Ffx" GPU sorting code is [AMD FidelityFX ParallelSort](https://github.com/GPUOpen-Effects/FidelityFX-ParallelSort), ported to Unity by me.
