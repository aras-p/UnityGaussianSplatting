# IRC HSLU

This is a customized version of the original Unity plugin by aras-p. The changes that we make are mostly experimental to see what works for us in implementing immersive experiences based on 3D Gaussian Splatting.

# Gaussian Splatting playground in Unity

SIGGRAPH 2023 had a paper "[**3D Gaussian Splatting for Real-Time Radiance Field Rendering**](https://repo-sam.inria.fr/fungraph/3d-gaussian-splatting/)" by Kerbl, Kopanas, Leimkühler, Drettakis
that is really cool! Check out their website, source code repository, data sets and so on. I've decided to try to implement the realtime visualization part (i.e. the one that takes already-produced
gaussian splat "model" file) in Unity.

![Screenshot](/docs/Images/shotOverview.jpg?raw=true "Screenshot")

Everything in this repository is based on that "OG" gaussian splatting paper. Towards end of 2023, there's a ton of
[new gaussian splatting research](https://github.com/MrNeRF/awesome-3D-gaussian-splatting) coming out; _none_ of that is in this project.

Status as of 2023 December is that I'm not planning on any significant further developments.

## Usage

:warning: Note: this is all _**a toy**_ that I'm just playing around with. If you file bugs or feature requests, I will most likely just ignore them. I told you so! :warning:

You can install the package via the Unity package manager by installing it via the URL `https://github.com/irc-hslu/UnityGaussianSplatting.git?path=/package`.


As an alternative, you can also clone this repository, open `projects/GaussianExample` as a Unity project (I use Unity 2022.3, other versions might also work),
and open `GSTestScene` scene in there.

Note that the project requires DX12 or Vulkan on Windows, i.e. **DX11 will not work**. This is **not tested at all on mobile/web**, and probably
does not work there.

<img align="right" src="docs/Images/shotAssetCreator.png" width="250px">

Next up, **create some GaussianSplat assets**: open `Tools -> Gaussian Splats -> Create GaussianSplatAsset` menu within Unity.
In the dialog, point `Input PLY File` to your Gaussian Splat file (note that it has to be a gaussian splat PLY file, not some 
other PLY file. E.g. in the official paper models, the correct files are under `point_cloud/iteration_*/point_cloud.ply`).
Optionally there can be `cameras.json` next to it or somewhere in parent folders.

Pick desired compression options and output folder, and press "Create Asset" button. The compression even at "very low" quality setting is decently usable, e.g. 
this capture at Very Low preset is under 8MB of total size (click to see the video): \
[![Watch the video](https://img.youtube.com/vi/iccfV0YlWVI/0.jpg)](https://youtu.be/iccfV0YlWVI)

If everything was fine, there should be a GaussianSplat asset that has several data files next to it.

Since the gaussian splat models are quite large, I have not included any in this Github repo. The original
[paper github page](https://github.com/graphdeco-inria/gaussian-splatting) has a a link to
[14GB zip](https://repo-sam.inria.fr/fungraph/3d-gaussian-splatting/datasets/pretrained/models.zip) of their models.


In the game object that has a `GaussianSplatRenderer` script, **point the Asset field to** one of your created assets.
There are various controls on the script to debug/visualize the data, as well as a slider to move game camera into one of asset's camera
locations.

The rendering takes game object transformation matrix into account; the official gaussian splat models seem to be all rotated by about
-160 degrees around X axis, and mirrored around Z axis, so in the sample scene the object has such a transform set up.

Additional documentation:

* [Render Pipeline Integration](/docs/render-pipeline-integration.md)
* [Editing Splats](/docs/splat-editing.md)

_That's it!_


## Write-ups

My own blog posts about all this:
* [Gaussian Splatting is pretty cool!](https://aras-p.info/blog/2023/09/05/Gaussian-Splatting-is-pretty-cool/) (2023 Sep 5)
* [Making Gaussian Splats smaller](https://aras-p.info/blog/2023/09/13/Making-Gaussian-Splats-smaller/) (2023 Sep 13)
* [Making Gaussian Splats more smaller](https://aras-p.info/blog/2023/09/27/Making-Gaussian-Splats-more-smaller/) (2023 Sep 27)
* [Gaussian Explosion](https://aras-p.info/blog/2023/12/08/Gaussian-explosion/) (2023 Dec 8)

## Performance numbers:

"bicycle" scene from the paper, with 6.1M splats and first camera in there, rendering at 1200x797 resolution,
at "Medium" asset quality level (282MB asset file):

* Windows (NVIDIA RTX 3080 Ti):
  * Official SBIR viewer: 7.4ms (135FPS). 4.8GB VRAM usage.
  * Unity, DX12 or Vulkan: 6.8ms (147FPS) - 4.5ms rendering, 1.1ms sorting, 0.8ms splat view calc. 1.3GB VRAM usage.
* Mac (Apple M1 Max):
  * Unity, Metal: 21.5ms (46FPS).

Besides the gaussian splat asset that is loaded into GPU memory, currently this also needs about 48 bytes of GPU memory
per splat (for sorting, caching view dependent data etc.).


## License and External Code Used

The code I wrote for this is under MIT license. The project also uses several 3rd party libraries:

- [zanders3/json](https://github.com/zanders3/json), MIT license, (c) 2018 Alex Parker.
- "DeviceRadixSort" GPU sorting code contributed by Thomas Smith ([#82](https://github.com/aras-p/UnityGaussianSplatting/pull/82)).

However, keep in mind that the [license of the original paper implementation](https://github.com/graphdeco-inria/gaussian-splatting/blob/main/LICENSE.md)
says that the official _training_ software for the Gaussian Splats is for educational / academic / non-commercial
purpose; commercial usage requires getting license from INRIA. That is: even if this viewer / integration
into Unity is just "MIT license", you need to separately consider *how* did you get your Gaussian Splat PLY files.
