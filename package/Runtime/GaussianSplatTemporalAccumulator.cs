// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace GaussianSplatting.Runtime
{
    // Very simple temporal accumulator: just blend in new frames.
    // Whenever scene or camera changes, restart the blending.
    // There's no reprojection or any similar cleverness.
    class GaussianSplatTemporalAccumulator : IDisposable
    {
        const int k_MaxFrames = 64;

        class History : IDisposable
        {
            public RenderTexture previous, next;
            public int sceneHash, frames;

            public void Dispose()
            {
                Object.DestroyImmediate(previous);
                Object.DestroyImmediate(next);
            }
        }

        static readonly int s_History = Shader.PropertyToID("_SplatHistory");
        static readonly int s_FrameWeight = Shader.PropertyToID("_SplatFrameWeight");
        readonly Dictionary<Camera, History> m_Histories = new();
        readonly List<Camera> m_DeadCameras = new();
        readonly MaterialPropertyBlock m_Properties = new();

        public void Dispose()
        {
            foreach (var history in m_Histories.Values)
                history.Dispose();
            m_Histories.Clear();
        }

        public void Reset(Camera camera)
        {
            if (m_Histories.Remove(camera, out var history))
                history.Dispose();
        }

        public void Render(CommandBuffer cmb, Camera camera, Material material, int sceneHash)
        {
            // handle potentially gone cameras
            foreach (var cam in m_Histories.Keys)
            {
                if (!cam)
                    m_DeadCameras.Add(cam);
            }
            foreach (var cam in m_DeadCameras)
                Reset(cam);
            m_DeadCameras.Clear();

            int width = camera.pixelWidth, height = camera.pixelHeight;
            if (m_Histories.TryGetValue(camera, out var history) &&
                (history.previous.width != width || history.previous.height != height))
            {
                Reset(camera);
                history = null;
            }
            if (history == null)
            {
                history = new History();
                var desc = new RenderTextureDescriptor(width, height, GraphicsFormat.R16G16B16A16_SFloat, 0);
                history.previous = new RenderTexture(desc) {name = "Splat history", hideFlags = HideFlags.HideAndDontSave};
                history.next = new RenderTexture(desc) {name = "Splat accumulation", hideFlags = HideFlags.HideAndDontSave};
                history.previous.Create();
                history.next.Create();
                m_Histories.Add(camera, history);
            }

            int hash = HashCode.Combine(sceneHash, camera.worldToCameraMatrix, camera.projectionMatrix,
                camera.pixelRect, camera.cullingMask, camera.targetTexture);
            if (history.sceneHash != hash)
            {
                history.frames = 0;
                history.sceneHash = hash;
            }

            if (history.frames < k_MaxFrames)
            {
                m_Properties.Clear();
                m_Properties.SetTexture(s_History, history.previous);
                m_Properties.SetFloat(s_FrameWeight, 1.0f / ++history.frames);
                cmb.SetRenderTarget(history.next);
                cmb.DrawProcedural(Matrix4x4.identity, material, 1, MeshTopology.Triangles, 3, 1, m_Properties);
                (history.previous, history.next) = (history.next, history.previous);
            }

            m_Properties.Clear();
            m_Properties.SetTexture(GaussianSplatRenderer.Props.GaussianSplatRT, history.previous);
            cmb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            cmb.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3, 1, m_Properties);
#if UNITY_EDITOR
            // in scene view, keep on repainting it, until converges to maximum # of frames
            if (!Application.isPlaying && camera.cameraType == CameraType.SceneView && history.frames < k_MaxFrames)
                UnityEditor.SceneView.RepaintAll();
#endif
        }
    }
}
