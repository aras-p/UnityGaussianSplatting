using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.XR;
using Object = UnityEngine.Object;

namespace GaussianSplatting.Runtime
{
    public class GaussianSplatTemporalFilter
    {
        static class Props
        {
            public static readonly int _TaaAccumulationTex = Shader.PropertyToID("_TaaAccumulationTex");
            public static readonly int _TaaFrameInfluence     = Shader.PropertyToID("_TaaFrameInfluence");
            public static readonly int _TaaVarianceClampScale = Shader.PropertyToID("_TaaVarianceClampScale");
        }

        int m_CurWidth = -1, m_CurHeight = -1;
        RenderTexture m_AccumulationTexture;
        RenderTexture m_TempTexture;

        public void Dispose()
        {
            Object.DestroyImmediate(m_AccumulationTexture); m_AccumulationTexture = null;
            Object.DestroyImmediate(m_TempTexture); m_TempTexture = null;
            m_CurWidth = -1;
            m_CurHeight = -1;
        }

        public void Render(
            CommandBuffer cmb,
            Camera camera,
            Material material,
            int passIndex,
            RenderTargetIdentifier srcSplatColor,
            RenderTargetIdentifier dstComposedColor,
            float frameInfluence,
            float varianceClampScale)
        {
            int screenW = camera.pixelWidth, screenH = camera.pixelHeight;
            int eyeW = XRSettings.eyeTextureWidth, eyeH = XRSettings.eyeTextureHeight;
            int width = eyeW != 0 ? eyeW : screenW;
            int height = eyeH != 0 ? eyeH : screenH;

            float taaFrameInfluence = frameInfluence;

            if (width != m_CurWidth || height != m_CurHeight || m_AccumulationTexture == null || m_TempTexture == null)
            {
                Object.DestroyImmediate(m_AccumulationTexture);
                Object.DestroyImmediate(m_TempTexture);
                m_CurWidth = width;
                m_CurHeight = height;

                RenderTextureDescriptor desc = default;
                desc.width = m_CurWidth;
                desc.height = m_CurHeight;
                desc.msaaSamples = 1;
                desc.volumeDepth = 1;
                desc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                desc.dimension = TextureDimension.Tex2D;
                m_AccumulationTexture = new RenderTexture(desc);
                m_TempTexture = new RenderTexture(desc);
                taaFrameInfluence = 1.0f; // copy input into history when initializing/resizing
            }

            // sample new frame & history -> output temp buffer
            cmb.SetRenderTarget(m_TempTexture);
            material.SetFloat(Props._TaaFrameInfluence, taaFrameInfluence);
            material.SetFloat(Props._TaaVarianceClampScale, varianceClampScale);
            material.SetTexture(Props._TaaAccumulationTex, m_AccumulationTexture);
            cmb.DrawProcedural(Matrix4x4.identity, material, passIndex, MeshTopology.Triangles, 3, 1);

            // copy temp buffer -> into history
            cmb.CopyTexture(m_TempTexture, m_AccumulationTexture);

            // composite temp buffer into output
            cmb.CopyTexture(m_TempTexture, srcSplatColor);
            cmb.SetRenderTarget(dstComposedColor);
            cmb.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3, 1);
        }
    }
}