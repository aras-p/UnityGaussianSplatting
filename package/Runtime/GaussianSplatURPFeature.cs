// SPDX-License-Identifier: MIT
#if GS_ENABLE_URP

#if !UNITY_6000_0_OR_NEWER
#error Unity Gaussian Splatting URP support only works in Unity 6 or later
#endif

using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.XR;

namespace GaussianSplatting.Runtime
{
    // Note: I have no idea what is the purpose of ScriptableRendererFeature vs ScriptableRenderPass, which one of those
    // is supposed to do resource management vs logic, etc. etc. Code below "seems to work" but I'm just fumbling along,
    // without understanding any of it.
    //
    // ReSharper disable once InconsistentNaming
    class GaussianSplatURPFeature : ScriptableRendererFeature
    {
        class GSRenderPass : ScriptableRenderPass
        {
            const string GaussianSplatRTName = "_GaussianSplatRT";

            const string ProfilerTag = "GaussianSplatRenderGraph";
            static readonly ProfilingSampler s_profilingSampler = new(ProfilerTag);
            static readonly int s_gaussianSplatRT = Shader.PropertyToID(GaussianSplatRTName);

            class PassData
            {
                internal UniversalCameraData CameraData;
                internal TextureHandle SourceTexture;
                internal TextureHandle SourceDepth;
                internal TextureHandle GaussianSplatRT;
                internal bool IsStereo;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                using var builder = renderGraph.AddUnsafePass(ProfilerTag, out PassData passData);

                var cameraData = frameData.Get<UniversalCameraData>();
                var resourceData = frameData.Get<UniversalResourceData>();

                bool isStereo = XRSettings.enabled && cameraData.camera.stereoEnabled && 
                                (XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.SinglePassInstanced || 
                                 XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.SinglePassMultiview);
                RenderTextureDescriptor rtDesc = isStereo? XRSettings.eyeTextureDesc: cameraData.cameraTargetDescriptor;
                rtDesc.depthBufferBits = 0;
                rtDesc.msaaSamples = 1;
                rtDesc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                
                // Create render texture
                var gaussianSplatRT = UniversalRenderer.CreateRenderGraphTexture(renderGraph, rtDesc, GaussianSplatRTName, true);

                passData.CameraData = cameraData;
                passData.SourceTexture = resourceData.activeColorTexture;
                passData.SourceDepth = resourceData.activeDepthTexture;
                passData.GaussianSplatRT = gaussianSplatRT;
                passData.IsStereo = isStereo;

                builder.UseTexture(resourceData.activeColorTexture, AccessFlags.ReadWrite);
                builder.UseTexture(resourceData.activeDepthTexture);
                builder.UseTexture(gaussianSplatRT, AccessFlags.ReadWrite);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
                {
                    var commandBuffer = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    using var _ = new ProfilingScope(commandBuffer, s_profilingSampler);
                    
                    if (data.IsStereo)
                    {
                        // Check if any of the active splats require per-eye sorting
                        bool requiresPerEyeSorting = GaussianSplatRenderSystem.instance.RequiresPerEyeSorting();
                        Material matComposite = null;

                        if (requiresPerEyeSorting)
                        {
                            // Per-eye sorting mode - sort and render each eye separately
                            CoreUtils.SetRenderTarget(commandBuffer, data.GaussianSplatRT, ClearFlag.Color, Color.clear);

                            // Left eye
                            CoreUtils.SetRenderTarget(commandBuffer, data.GaussianSplatRT, ClearFlag.Color, Color.clear, 0, CubemapFace.Unknown, 0);
                            matComposite = GaussianSplatRenderSystem.instance.SortAndRenderSplats(data.CameraData.camera, commandBuffer, 0);

                            // Right eye
                            CoreUtils.SetRenderTarget(commandBuffer, data.GaussianSplatRT, ClearFlag.Color, Color.clear, 0, CubemapFace.Unknown, 1);
                            GaussianSplatRenderSystem.instance.SortAndRenderSplats(data.CameraData.camera, commandBuffer, 1);
                        }
                        else
                        {
                            // Standard stereo rendering - sort once, render twice
                            // Clear the render target for both eyes
                            CoreUtils.SetRenderTarget(commandBuffer, data.GaussianSplatRT, ClearFlag.Color, Color.clear);

                            // Prepare the splats once - sort them and calculate view data
                            var renderData = GaussianSplatRenderSystem.instance.PrepareSplats(data.CameraData.camera, commandBuffer);
                            
                            // [Quest3] Workaround for stereo rendering. Unity is not able to correctly set unity_stereoEyeIndex when drawing to
                            // a render texture array, so we need to do it manually. Also, we need to draw the same material twice,
                            // once for each eye. TODO: Revisit this when Unity fixes the issue.

                            // Render to left eye
                            CoreUtils.SetRenderTarget(commandBuffer, data.GaussianSplatRT, ClearFlag.Color, Color.clear, 0, CubemapFace.Unknown, 0);
                            GaussianSplatRenderSystem.instance.RenderPreparedSplats(commandBuffer, 0);

                            // Render to right eye
                            CoreUtils.SetRenderTarget(commandBuffer, data.GaussianSplatRT, ClearFlag.Color, Color.clear, 0, CubemapFace.Unknown, 1);
                            GaussianSplatRenderSystem.instance.RenderPreparedSplats(commandBuffer, 1);
                            matComposite = renderData.matComposite;
                        }

                        // Composite to the final target
                        commandBuffer.BeginSample(GaussianSplatRenderSystem.s_ProfCompose);
                        matComposite.SetTexture(s_gaussianSplatRT, data.GaussianSplatRT);

                        // [Quest3] Workaround for stereo rendering. Unity is not able to correctly set unity_stereoEyeIndex when drawing to
                        // a render texture array, so we need to do it manually. Also, we need to draw the same material twice,
                        // once for each eye. TODO: Revisit this when Unity fixes the issue.
                        commandBuffer.SetRenderTarget(data.SourceTexture, 0, CubemapFace.Unknown, 0);
                        commandBuffer.SetGlobalInt("_CustomStereoEyeIndex", 0); // emulate left
                        commandBuffer.DrawProcedural(Matrix4x4.identity, matComposite, 0, MeshTopology.Triangles, 3, 1);

                        commandBuffer.SetRenderTarget(data.SourceTexture, 0, CubemapFace.Unknown, 1);
                        commandBuffer.SetGlobalInt("_CustomStereoEyeIndex", 1); // emulate right
                        commandBuffer.DrawProcedural(Matrix4x4.identity, matComposite, 0, MeshTopology.Triangles, 3, 1);
                        commandBuffer.EndSample(GaussianSplatRenderSystem.s_ProfCompose);
                    }
                    else
                    {
                        // Single-eye rendering
                        commandBuffer.SetGlobalTexture(s_gaussianSplatRT, data.GaussianSplatRT);
                        CoreUtils.SetRenderTarget(commandBuffer, data.GaussianSplatRT, data.SourceDepth, ClearFlag.Color, Color.clear);
                        Material matComposite = GaussianSplatRenderSystem.instance.SortAndRenderSplats(data.CameraData.camera, commandBuffer);
                        
                        commandBuffer.BeginSample(GaussianSplatRenderSystem.s_ProfCompose);
                        Blitter.BlitCameraTexture(commandBuffer, data.GaussianSplatRT, data.SourceTexture, matComposite, 0);
                        commandBuffer.EndSample(GaussianSplatRenderSystem.s_ProfCompose);
                    }
                });
            }
        }

        GSRenderPass m_Pass;
        bool m_HasCamera;

        public override void Create()
        {
            m_Pass = new GSRenderPass
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents
            };
        }

        public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
        {
            m_HasCamera = false;
            var system = GaussianSplatRenderSystem.instance;
            if (!system.GatherSplatsForCamera(cameraData.camera))
                return;

            m_HasCamera = true;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!m_HasCamera)
                return;
            renderer.EnqueuePass(m_Pass);
        }

        protected override void Dispose(bool disposing)
        {
            m_Pass = null;
        }
    }
}

#endif // #if GS_ENABLE_URP
