// SPDX-License-Identifier: MIT
#if GS_ENABLE_HDRP

using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;

namespace GaussianSplatting.Runtime
{
    // Note: I have no idea what is the proper usage of CustomPass.
    // Code below "seems to work" but I'm just fumbling along, without understanding any of it.
    class GaussianSplatHDRPPass : CustomPass
    {
        RTHandle m_RenderTarget;

        // It can be used to configure render targets and their clear state. Also to create temporary render target textures.
        // When empty this render pass will render to the active camera render target.
        // You should never call CommandBuffer.SetRenderTarget. Instead call <c>ConfigureTarget</c> and <c>ConfigureClear</c>.
        // The render pipeline will ensure target setup and clearing happens in an performance manner.
        protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd)
        {
            m_RenderTarget = RTHandles.Alloc(Vector2.one,
                colorFormat: GraphicsFormat.R16G16B16A16_SFloat, useDynamicScale: true,
                depthBufferBits: DepthBits.None, msaaSamples: MSAASamples.None,
                filterMode: FilterMode.Point, wrapMode: TextureWrapMode.Clamp, name: "_GaussianSplatRT");
        }

        protected override void Execute(CustomPassContext ctx)
        {
            var cam = ctx.hdCamera.camera;

            var system = GaussianSplatRenderSystem.instance;
            if (!system.GatherSplatsForCamera(cam))
                return;

            ctx.cmd.SetGlobalTexture(m_RenderTarget.name, m_RenderTarget.nameID);
            CoreUtils.SetRenderTarget(ctx.cmd, m_RenderTarget, ctx.cameraDepthBuffer, ClearFlag.Color,
                new Color(0, 0, 0, 0));

            // add sorting, view calc and drawing commands for each splat object
            Material matComposite =
                GaussianSplatRenderSystem.instance.SortAndRenderSplats(ctx.hdCamera.camera, ctx.cmd);

            // compose
            ctx.cmd.BeginSample(GaussianSplatRenderSystem.s_ProfCompose);
            CoreUtils.SetRenderTarget(ctx.cmd, ctx.cameraColorBuffer, ClearFlag.None);
            CoreUtils.DrawFullScreen(ctx.cmd, matComposite, ctx.propertyBlock, shaderPassId: 0);
            ctx.cmd.EndSample(GaussianSplatRenderSystem.s_ProfCompose);
        }

        protected override void Cleanup()
        {
            m_RenderTarget.Release();
        }
    }
}

#endif
