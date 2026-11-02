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
    [System.Serializable]
    class GaussianSplatHDRPPass : CustomPass
    {
        RTHandle m_RenderTarget;
        RTHandle m_DepthTarget;
        GaussianSplatTemporalAccumulator m_Temporal;

        protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd)
        {
            m_Temporal = new GaussianSplatTemporalAccumulator();
            m_RenderTarget = RTHandles.Alloc(Vector2.one,
                colorFormat: GraphicsFormat.R16G16B16A16_SFloat, useDynamicScale: true,
                depthBufferBits: DepthBits.None, msaaSamples: MSAASamples.None,
                filterMode: FilterMode.Point, wrapMode: TextureWrapMode.Clamp, name: "_GaussianSplatRT");
        }

        void EnsureDepthTarget()
        {
            m_DepthTarget ??= RTHandles.Alloc(Vector2.one,
                colorFormat: GraphicsFormat.R32_SFloat, useDynamicScale: true,
                depthBufferBits: DepthBits.None, msaaSamples: MSAASamples.None,
                filterMode: FilterMode.Point, wrapMode: TextureWrapMode.Clamp, name: "_GaussianSplatDepthRT");
        }

        protected override void Execute(CustomPassContext ctx)
        {
            var cam = ctx.hdCamera.camera;

            var system = GaussianSplatRenderSystem.instance;
            if (!system.GatherSplatsForCamera(cam))
            {
                m_Temporal.Reset(cam);
                return;
            }

            var filter = GaussianSplatRenderingOptions.instance.temporalFilter;
            int width = ctx.hdCamera.actualWidth, height = ctx.hdCamera.actualHeight;
            Matrix4x4 projection = ctx.hdCamera.mainViewConstants.projMatrix;

            ctx.cmd.SetGlobalTexture(m_RenderTarget.name, m_RenderTarget.nameID);
            ctx.cmd.SetGlobalVector(GaussianSplatRenderer.Props.CameraTargetTextureTexelSize, Vector4.zero);
            if (filter == StochasticTemporalFilter.Reprojected)
            {
                EnsureDepthTarget();
                ctx.cmd.SetGlobalTexture(GaussianSplatRenderer.Props.GaussianSplatDepthRT, m_DepthTarget);
                CoreUtils.SetRenderTarget(ctx.cmd, new[] { m_RenderTarget.nameID, m_DepthTarget.nameID },
                    ctx.cameraDepthBuffer, ClearFlag.Color, Color.clear);
            }
            else
                CoreUtils.SetRenderTarget(ctx.cmd, m_RenderTarget, ctx.cameraDepthBuffer, ClearFlag.Color, Color.clear);

            Material matComposite =
                system.SortAndRenderSplats(cam, ctx.cmd, projection, width, height);

            // compose
            ctx.cmd.BeginSample(GaussianSplatRenderSystem.s_ProfCompose);
            if (filter != StochasticTemporalFilter.None)
            {
                var filtered = m_Temporal.Accumulate(ctx.cmd, cam, matComposite, system.GetTemporalSceneHash(),
                    filter, width, height, projection * cam.worldToCameraMatrix);
                ctx.cmd.SetGlobalTexture(GaussianSplatRenderer.Props.GaussianSplatRT, filtered);
            }
            else
                m_Temporal.Reset(cam);
            CoreUtils.SetRenderTarget(ctx.cmd, ctx.cameraColorBuffer, ClearFlag.None);
            CoreUtils.DrawFullScreen(ctx.cmd, matComposite, ctx.propertyBlock, shaderPassId: 0);
            ctx.cmd.EndSample(GaussianSplatRenderSystem.s_ProfCompose);
        }

        protected override void Cleanup()
        {
            m_RenderTarget?.Release();
            m_DepthTarget?.Release();
            m_RenderTarget = null;
            m_DepthTarget = null;
            m_Temporal?.Dispose();
            m_Temporal = null;
        }
    }
}

#endif
