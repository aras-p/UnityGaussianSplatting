// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace GaussianSplatting.Runtime
{
    // Simple temporal filtering: average stationary frames,
    // optionally reproject history during camera motion
    class GaussianSplatTemporalAccumulator : IDisposable
    {
        const int k_MaxFrames = 64;

        internal class History : IDisposable
        {
            public RenderTexture previous, next;
            public int sceneHash, viewHash, frames;
            public bool orthographic;
            public Matrix4x4 viewProjection;
#if GS_ENABLE_URP
            public RTHandle previousHandle, nextHandle;
#endif

            public void Dispose()
            {
#if GS_ENABLE_URP
                previousHandle?.Release();
                nextHandle?.Release();
#endif
                Object.DestroyImmediate(previous);
                Object.DestroyImmediate(next);
            }
        }

        static readonly int s_RenderSize = Shader.PropertyToID("_SplatRenderSize");
        static readonly int s_History = Shader.PropertyToID("_SplatHistory");
        static readonly int s_FrameWeight = Shader.PropertyToID("_SplatFrameWeight");
        static readonly int s_InvViewProjection = Shader.PropertyToID("_SplatInvViewProjection");
        static readonly int s_PreviousViewProjection = Shader.PropertyToID("_SplatPreviousViewProjection");
        static readonly int s_CameraPosition = Shader.PropertyToID("_SplatCameraPosition");
        static readonly int s_Orthographic = Shader.PropertyToID("_SplatOrthographic");
        readonly Dictionary<(Camera camera, int eye), History> m_Histories = new();
        readonly List<(Camera camera, int eye)> m_DeadCameras = new();
        readonly MaterialPropertyBlock m_Properties = new();

        public void Dispose()
        {
            foreach (var history in m_Histories.Values)
                history.Dispose();
            m_Histories.Clear();
        }

        public void Reset(Camera camera)
        {
            foreach (var key in m_Histories.Keys)
            {
                if (key.camera == camera)
                    m_DeadCameras.Add(key);
            }
            foreach (var key in m_DeadCameras)
                Reset(key);
            m_DeadCameras.Clear();
        }

        void Reset((Camera camera, int eye) key)
        {
            if (m_Histories.Remove(key, out var history))
                history.Dispose();
        }

        // regular non-XR rendering uses eye 0
        internal History Prepare(Camera camera, int width, int height, int eye = 0)
        {
            // handle potentially gone cameras
            foreach (var key in m_Histories.Keys)
            {
                if (!key.camera)
                    m_DeadCameras.Add(key);
            }
            foreach (var key in m_DeadCameras)
                Reset(key);
            m_DeadCameras.Clear();

            var cameraEye = (camera, eye);
            if (m_Histories.TryGetValue(cameraEye, out var history) &&
                (history.previous.width != width || history.previous.height != height))
            {
                Reset(cameraEye);
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
#if GS_ENABLE_URP
                history.previousHandle = RTHandles.Alloc(history.previous);
                history.nextHandle = RTHandles.Alloc(history.next);
#endif
                m_Histories.Add(cameraEye, history);
            }

            return history;
        }

        public RenderTexture Accumulate(CommandBuffer cmb, Camera camera, Material material, int sceneHash,
            StochasticTemporalFilter filter, int width, int height, Matrix4x4 viewProjection, int eye = 0, Matrix4x4? view = null)
        {
            var history = Prepare(camera, width, height, eye);
            Matrix4x4 viewMatrix = view ?? camera.worldToCameraMatrix;
            int hash = HashCode.Combine(sceneHash, filter, camera.pixelRect, camera.cullingMask, camera.targetTexture);
            // note: use non-jittered camera projection; jitter should not
            // reset our history
            int viewHash = HashCode.Combine(viewMatrix, camera.nonJitteredProjectionMatrix);
            bool valid = history.frames > 0 && history.sceneHash == hash && history.orthographic == camera.orthographic;
            bool moved = history.viewHash != viewHash;
            bool reproject = valid && history.viewProjection != viewProjection && filter == StochasticTemporalFilter.Reprojected;
            // after motion, the reprojected result starts a fresh stationary accumulation
            if (!valid || moved || reproject)
                history.frames = 0;

            if (history.frames < k_MaxFrames)
            {
                m_Properties.Clear();
                m_Properties.SetTexture(s_History, history.previous);
                m_Properties.SetFloat(s_FrameWeight, 1.0f / ++history.frames);
                if (reproject)
                {
                    m_Properties.SetVector(s_RenderSize, new Vector4(width, height, 0, 0));
                    m_Properties.SetMatrix(s_InvViewProjection, viewProjection.inverse);
                    m_Properties.SetMatrix(s_PreviousViewProjection, history.viewProjection);
                    m_Properties.SetVector(s_CameraPosition, viewMatrix.inverse.GetColumn(3));
                    m_Properties.SetInteger(s_Orthographic, camera.orthographic ? 1 : 0);
                }
                cmb.SetRenderTarget(history.next);
                cmb.SetViewport(new Rect(0, 0, width, height));
                cmb.SetKeyword(material, new LocalKeyword(material.shader, "SPLAT_REPROJECTED"), reproject);
                cmb.DrawProcedural(Matrix4x4.identity, material, 1, MeshTopology.Triangles, 3, 1, m_Properties);
                (history.previous, history.next) = (history.next, history.previous);
#if GS_ENABLE_URP
                (history.previousHandle, history.nextHandle) = (history.nextHandle, history.previousHandle);
#endif
            }
            history.sceneHash = hash;
            history.viewHash = viewHash;
            history.orthographic = camera.orthographic;
            history.viewProjection = viewProjection;

#if UNITY_EDITOR
            // in scene view, keep on repainting it, until converges to maximum # of frames
            if (!Application.isPlaying && camera.cameraType == CameraType.SceneView && history.frames < k_MaxFrames)
                UnityEditor.SceneView.RepaintAll();
#endif
            return history.previous;
        }
    }
}
