// SPDX-License-Identifier: MIT
#if GS_ENABLE_URP

using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Note: I have no idea what is the purpose of ScriptableRendererFeature vs ScriptableRenderPass, which one of those
// is supposed to do resource management vs logic, etc. etc. Code below "seems to work" but I'm just fumbling along,
// without understanding any of it.
class GaussianSplatURPFeature : ScriptableRendererFeature
{
    class GSRenderPass : ScriptableRenderPass
    {
        RTHandle m_RenderTarget;
        internal ScriptableRenderer m_Renderer = null;
        internal CommandBuffer m_Cmb = null;

        public void Dispose()
        {
            m_RenderTarget?.Release();
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            RenderTextureDescriptor rtDesc = renderingData.cameraData.cameraTargetDescriptor;
            rtDesc.depthBufferBits = 0;
            rtDesc.msaaSamples = 1;
            rtDesc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
            RenderingUtils.ReAllocateIfNeeded(ref m_RenderTarget, rtDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "_GaussianSplatRT");
            cmd.SetGlobalTexture(m_RenderTarget.name, m_RenderTarget.nameID);

            ConfigureTarget(m_RenderTarget, m_Renderer.cameraDepthTargetHandle);
            ConfigureClear(ClearFlag.Color, new Color(0,0,0,0));
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (m_Cmb == null)
                return;

            // add sorting, view calc and drawing commands for each splat object
            Material matComposite = GaussianSplatRenderSystem.instance.SortAndRenderSplats(renderingData.cameraData.camera, m_Cmb);

            // compose
            m_Cmb.BeginSample(GaussianSplatRenderSystem.s_ProfCompose);
            Blitter.BlitCameraTexture(m_Cmb, m_RenderTarget, m_Renderer.cameraColorTargetHandle, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store, matComposite, 0);
            m_Cmb.EndSample(GaussianSplatRenderSystem.s_ProfCompose);
            context.ExecuteCommandBuffer(m_Cmb);
        }
    }

    GSRenderPass m_Pass;

    public override void Create()
    {
        m_Pass = new GSRenderPass
        {
            renderPassEvent = RenderPassEvent.BeforeRenderingTransparents
        };
    }

    public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
    {
        var system = GaussianSplatRenderSystem.instance;
        if (!system.GatherSplatsForCamera(cameraData.camera))
            return;

        CommandBuffer cmb = system.InitialClearCmdBuffer(cameraData.camera);
        m_Pass.m_Cmb = cmb;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        m_Pass.m_Renderer = renderer;
        renderer.EnqueuePass(m_Pass);
    }

    protected override void Dispose(bool disposing)
    {
        m_Pass?.Dispose();
        m_Pass = null;
    }
}

#endif // #if GS_ENABLE_URP
