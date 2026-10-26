// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace GaussianSplatting.Runtime
{
    class GaussianSplatRenderSystem
    {
        // ReSharper disable MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        internal static readonly ProfilerMarker s_ProfDraw = new(ProfilerCategory.Render, "GaussianSplat.Draw", MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker s_ProfCompose = new(ProfilerCategory.Render, "GaussianSplat.Compose", MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker s_ProfCalcView = new(ProfilerCategory.Render, "GaussianSplat.CalcView", MarkerFlags.SampleGPU);
        // ReSharper restore MemberCanBePrivate.Global

        public static GaussianSplatRenderSystem instance => ms_Instance ??= new GaussianSplatRenderSystem();
        static GaussianSplatRenderSystem ms_Instance;

        readonly Dictionary<GaussianSplatRenderer, MaterialPropertyBlock> m_Splats = new();
        readonly HashSet<Camera> m_CameraCommandBuffersDone = new();
        readonly List<(GaussianSplatRenderer, MaterialPropertyBlock)> m_ActiveSplats = new();

        CommandBuffer m_CommandBuffer;

        public void RegisterSplat(GaussianSplatRenderer r)
        {
            if (m_Splats.Count == 0)
            {
                if (GraphicsSettings.currentRenderPipeline == null)
                    Camera.onPreCull += OnPreCullCamera;
            }

            m_Splats.Add(r, new MaterialPropertyBlock());
        }

        public void UnregisterSplat(GaussianSplatRenderer r)
        {
            if (!m_Splats.ContainsKey(r))
                return;
            m_Splats.Remove(r);
            if (m_Splats.Count == 0)
            {
                if (m_CameraCommandBuffersDone != null)
                {
                    if (m_CommandBuffer != null)
                    {
                        foreach (var cam in m_CameraCommandBuffersDone)
                        {
                            if (cam)
                                cam.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, m_CommandBuffer);
                        }
                    }
                    m_CameraCommandBuffersDone.Clear();
                }

                m_ActiveSplats.Clear();
                m_CommandBuffer?.Dispose();
                m_CommandBuffer = null;
                Camera.onPreCull -= OnPreCullCamera;
            }
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public bool GatherSplatsForCamera(Camera cam)
        {
            if (cam.cameraType == CameraType.Preview)
                return false;
            // gather all active & valid splat objects
            m_ActiveSplats.Clear();
            foreach (var kvp in m_Splats)
            {
                var gs = kvp.Key;
                if (gs == null || !gs.isActiveAndEnabled || !gs.HasValidRenderSetup)
                    continue;
                m_ActiveSplats.Add((kvp.Key, kvp.Value));
            }
            if (m_ActiveSplats.Count == 0)
                return false;

            // sort them by order and depth from camera
            var camTr = cam.transform;
            m_ActiveSplats.Sort((a, b) =>
            {
                var orderA = a.Item1.m_RenderOrder;
                var orderB = b.Item1.m_RenderOrder;
                if (orderA != orderB)
                    return orderB.CompareTo(orderA);
                var trA = a.Item1.transform;
                var trB = b.Item1.transform;
                var posA = camTr.InverseTransformPoint(trA.position);
                var posB = camTr.InverseTransformPoint(trB.position);
                return posA.z.CompareTo(posB.z);
            });

            return true;
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public Material SortAndRenderSplats(Camera cam, CommandBuffer cmb)
        {
            Material matComposite = null;
            foreach (var kvp in m_ActiveSplats)
            {
                var gs = kvp.Item1;
                gs.EnsureMaterials();
                matComposite = gs.m_MatComposite;
                var mpb = kvp.Item2;

                // sort
                var matrix = gs.transform.localToWorldMatrix;
                if (gs.m_FrameCounter % gs.m_SortNthFrame == 0)
                    gs.SortPoints(cmb, cam, matrix);
                ++gs.m_FrameCounter;

                // cache view
                kvp.Item2.Clear();
                Material displayMat = gs.m_RenderMode switch
                {
                    GaussianSplatRenderer.RenderMode.DebugPoints => gs.m_MatDebugPoints,
                    GaussianSplatRenderer.RenderMode.DebugPointIndices => gs.m_MatDebugPoints,
                    GaussianSplatRenderer.RenderMode.DebugBoxes => gs.m_MatDebugBoxes,
                    _ => gs.m_MatSplats
                };
                if (displayMat == null)
                    continue;

                gs.SetDataOnMaterial(mpb);

                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatViewData, gs.m_GpuView);

                mpb.SetBuffer(GaussianSplatRenderer.Props.OrderBuffer, gs.m_GpuSortKeys);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatScale, gs.m_SplatScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatOpacityScale, gs.m_OpacityScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatSize, gs.m_PointDisplaySize);
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOrder, gs.m_SHOrder);
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOnly, gs.m_SHOnly ? 1 : 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayIndex, gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugPointIndices ? 1 : 0);

                cmb.BeginSample(s_ProfCalcView);
                gs.CalcViewData(cmb, cam);
                cmb.EndSample(s_ProfCalcView);

                // draw
                int indexCount = 6;
                int instanceCount = gs.splatCount;
                MeshTopology topology = MeshTopology.Triangles;
                if (gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugBoxes)
                    indexCount = 36;

                cmb.BeginSample(s_ProfDraw);
                cmb.DrawProcedural(gs.m_GpuIndexBuffer, matrix, displayMat, 0, topology, indexCount, instanceCount, mpb);
                cmb.EndSample(s_ProfDraw);
            }
            return matComposite;
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        // ReSharper disable once UnusedMethodReturnValue.Global - used by HDRP/URP features that are not always compiled
        public CommandBuffer InitialClearCmdBuffer(Camera cam)
        {
            m_CommandBuffer ??= new CommandBuffer {name = "RenderGaussianSplats"};
            if (GraphicsSettings.currentRenderPipeline == null && cam != null && !m_CameraCommandBuffersDone.Contains(cam))
            {
                cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, m_CommandBuffer);
                m_CameraCommandBuffersDone.Add(cam);
            }

            // get render target for all splats
            m_CommandBuffer.Clear();
            return m_CommandBuffer;
        }

        void OnPreCullCamera(Camera cam)
        {
            if (!GatherSplatsForCamera(cam))
            {
                m_CommandBuffer?.Clear();
                return;
            }

            InitialClearCmdBuffer(cam);

            m_CommandBuffer.GetTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT, -1, -1, 0, FilterMode.Point, GraphicsFormat.R16G16B16A16_SFloat);
            m_CommandBuffer.SetRenderTarget(GaussianSplatRenderer.Props.GaussianSplatRT, BuiltinRenderTextureType.CurrentActive);
            m_CommandBuffer.ClearRenderTarget(RTClearFlags.Color, new Color(0, 0, 0, 0), 0, 0);

            // We only need this to determine whether we're rendering into backbuffer or not. However, detection this
            // way only works in BiRP so only do it here.
            m_CommandBuffer.SetGlobalTexture(GaussianSplatRenderer.Props.CameraTargetTexture, BuiltinRenderTextureType.CameraTarget);

            // add sorting, view calc and drawing commands for each splat object
            Material matComposite = SortAndRenderSplats(cam, m_CommandBuffer);

            // compose
            m_CommandBuffer.BeginSample(s_ProfCompose);
            m_CommandBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            m_CommandBuffer.DrawProcedural(Matrix4x4.identity, matComposite, 0, MeshTopology.Triangles, 3, 1);
            m_CommandBuffer.EndSample(s_ProfCompose);
            m_CommandBuffer.ReleaseTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT);
        }
    }

    [ExecuteInEditMode]
    public class GaussianSplatRenderer : MonoBehaviour
    {
        public enum RenderMode
        {
            Splats,
            DebugPoints,
            DebugPointIndices,
            DebugBoxes,
        }
        [Tooltip("PLY/SPZ path relative to Assets/StreamingAssets") ]
        public string m_SourcePath;

        [Tooltip("Rendering order compared to other splats. Within same order splats are sorted by distance. Higher order splats render 'on top of' lower order splats.")]
        public int m_RenderOrder;
        [Range(0.1f, 2.0f)] [Tooltip("Additional scaling factor for the splats")]
        public float m_SplatScale = 1.0f;
        [Range(0.05f, 20.0f)]
        [Tooltip("Additional scaling factor for opacity")]
        public float m_OpacityScale = 1.0f;
        [Range(0, 3)] [Tooltip("Spherical Harmonics order to use")]
        public int m_SHOrder = 3;
        [Tooltip("Show only Spherical Harmonics contribution, using gray color")]
        public bool m_SHOnly;
        [Range(1,30)] [Tooltip("Sort splats only every N frames")]
        public int m_SortNthFrame = 1;

        public RenderMode m_RenderMode = RenderMode.Splats;
        [Range(1.0f,15.0f)] public float m_PointDisplaySize = 3.0f;

        public Shader m_ShaderSplats;
        public Shader m_ShaderComposite;
        public Shader m_ShaderDebugPoints;
        public Shader m_ShaderDebugBoxes;
        [Tooltip("Gaussian splatting compute shader")]
        public ComputeShader m_CSSplatUtilities;

        int m_SplatCount;
        GraphicsBuffer m_GpuSortDistances;
        internal GraphicsBuffer m_GpuSortKeys;
        GraphicsBuffer m_GpuPosData;
        GraphicsBuffer m_GpuOtherData;
        GraphicsBuffer m_GpuSHData;
        GraphicsBuffer m_GpuColorData;
        internal GraphicsBuffer m_GpuView;
        internal GraphicsBuffer m_GpuIndexBuffer;

        GpuSorting m_Sorter;
        GpuSorting.Args m_SorterArgs;

        internal Material m_MatSplats;
        internal Material m_MatComposite;
        internal Material m_MatDebugPoints;
        internal Material m_MatDebugBoxes;

        internal int m_FrameCounter;
        string m_PrevSourcePath;
        GaussianSplatLoadOperation m_LoadOperation;
        int m_SHCoefficients;
        bool m_Registered;

        public bool isLoading { get; private set; }
        public string loadError { get; private set; }
        public Bounds localBounds { get; private set; }
        public int sourceSHDegree { get; private set; }
        public GaussianSplatCameras.CameraInfo[] cameras { get; private set; } = Array.Empty<GaussianSplatCameras.CameraInfo>();

        static readonly ProfilerMarker s_ProfSort = new(ProfilerCategory.Render, "GaussianSplat.Sort", MarkerFlags.SampleGPU);

        internal static class Props
        {
            public static readonly int SplatPos = Shader.PropertyToID("_SplatPos");
            public static readonly int SplatOther = Shader.PropertyToID("_SplatOther");
            public static readonly int SplatSH = Shader.PropertyToID("_SplatSH");
            public static readonly int SplatColor = Shader.PropertyToID("_SplatColor");
            public static readonly int SHCoefficients = Shader.PropertyToID("_SHCoefficients");
            public static readonly int SplatViewData = Shader.PropertyToID("_SplatViewData");
            public static readonly int OrderBuffer = Shader.PropertyToID("_OrderBuffer");
            public static readonly int SplatScale = Shader.PropertyToID("_SplatScale");
            public static readonly int SplatOpacityScale = Shader.PropertyToID("_SplatOpacityScale");
            public static readonly int SplatSize = Shader.PropertyToID("_SplatSize");
            public static readonly int SplatCount = Shader.PropertyToID("_SplatCount");
            public static readonly int SHOrder = Shader.PropertyToID("_SHOrder");
            public static readonly int SHOnly = Shader.PropertyToID("_SHOnly");
            public static readonly int DisplayIndex = Shader.PropertyToID("_DisplayIndex");
            public static readonly int GaussianSplatRT = Shader.PropertyToID("_GaussianSplatRT");
            public static readonly int SplatSortKeys = Shader.PropertyToID("_SplatSortKeys");
            public static readonly int SplatSortDistances = Shader.PropertyToID("_SplatSortDistances");
            public static readonly int MatrixMV = Shader.PropertyToID("_MatrixMV");
            public static readonly int MatrixObjectToWorld = Shader.PropertyToID("_MatrixObjectToWorld");
            public static readonly int MatrixWorldToObject = Shader.PropertyToID("_MatrixWorldToObject");
            public static readonly int VecScreenParams = Shader.PropertyToID("_VecScreenParams");
            public static readonly int VecWorldSpaceCameraPos = Shader.PropertyToID("_VecWorldSpaceCameraPos");
            public static readonly int CameraTargetTexture = Shader.PropertyToID("_CameraTargetTexture");
        }

        public int splatCount => m_SplatCount;

        enum KernelIndices
        {
            SetIndices,
            CalcDistances,
            CalcViewData,
        }

        public bool HasValidRenderResources => resourcesAreSetUp;
        public bool HasValidRenderSetup => m_SplatCount > 0 && m_GpuView != null && m_GpuColorData != null;

        const int kGpuViewDataSize = 40;

        static GraphicsBuffer Upload(NativeArray<uint> data, string name)
        {
            var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, data.Length, 4) { name = name };
            try { buffer.SetData(data); return buffer; }
            catch { buffer.Dispose(); throw; }
        }

        void CreateResources(GaussianSplatData data)
        {
            m_SplatCount = data.count;
            sourceSHDegree = data.shDegree;
            m_SHCoefficients = data.shCoefficients;
            localBounds = data.bounds;
            m_GpuPosData = Upload(data.positions, "GaussianPosData");
            m_GpuOtherData = Upload(data.geometry, "GaussianOtherData");
            m_GpuColorData = Upload(data.colors, "GaussianColorData");
            m_GpuSHData = Upload(data.sh, "GaussianSHData");
            m_GpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SplatCount, kGpuViewDataSize);
            m_GpuIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, 36, 2);
            // cube indices, most often we use only the first quad
            m_GpuIndexBuffer.SetData(new ushort[]
            {
                0, 1, 2, 1, 3, 2,
                4, 6, 5, 5, 6, 7,
                0, 2, 4, 4, 2, 6,
                1, 5, 3, 5, 7, 3,
                0, 4, 1, 4, 5, 1,
                2, 3, 6, 3, 7, 6
            });

            InitSortBuffers(splatCount);
        }

        void InitSortBuffers(int count)
        {
            m_GpuSortDistances?.Dispose();
            m_GpuSortKeys?.Dispose();
            m_SorterArgs.resources.Dispose();

            EnsureSorterAndRegister();

            m_GpuSortDistances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4) { name = "GaussianSplatSortDistances" };
            m_GpuSortKeys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4) { name = "GaussianSplatSortIndices" };

            // init keys buffer to splat indices
            m_CSSplatUtilities.SetBuffer((int)KernelIndices.SetIndices, Props.SplatSortKeys, m_GpuSortKeys);
            m_CSSplatUtilities.SetInt(Props.SplatCount, m_GpuSortDistances.count);
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.SetIndices, out uint gsX, out _, out _);
            m_CSSplatUtilities.Dispatch((int)KernelIndices.SetIndices, (m_GpuSortDistances.count + (int)gsX - 1)/(int)gsX, 1, 1);

            m_SorterArgs.inputKeys = m_GpuSortDistances;
            m_SorterArgs.inputValues = m_GpuSortKeys;
            m_SorterArgs.count = (uint)count;
            if (m_Sorter.Valid)
                m_SorterArgs.resources = GpuSorting.SupportResources.Load((uint)count);
        }

        bool resourcesAreSetUp => m_ShaderSplats != null && m_ShaderComposite != null && m_ShaderDebugPoints != null &&
                                  m_ShaderDebugBoxes != null && m_CSSplatUtilities != null && SystemInfo.supportsComputeShaders;

        public void EnsureMaterials()
        {
            if (m_MatSplats == null && resourcesAreSetUp)
            {
                m_MatSplats = new Material(m_ShaderSplats) {name = "GaussianSplats"};
                m_MatComposite = new Material(m_ShaderComposite) {name = "GaussianClearDstAlpha"};
                m_MatDebugPoints = new Material(m_ShaderDebugPoints) {name = "GaussianDebugPoints"};
                m_MatDebugBoxes = new Material(m_ShaderDebugBoxes) {name = "GaussianDebugBoxes"};
            }
        }

        public void EnsureSorterAndRegister()
        {
            if (m_Sorter == null && resourcesAreSetUp)
            {
                m_Sorter = new GpuSorting(m_CSSplatUtilities);
            }

            if (!m_Registered && resourcesAreSetUp)
            {
                GaussianSplatRenderSystem.instance.RegisterSplat(this);
                m_Registered = true;
            }
        }

        public void OnEnable()
        {
            Reload();
        }

        public void Reload()
        {
            FinishLoading();
            m_PrevSourcePath = m_SourcePath;
            DisposeResources();
            loadError = null;
            cameras = Array.Empty<GaussianSplatCameras.CameraInfo>();
            m_FrameCounter = 0;
            if (!isActiveAndEnabled || string.IsNullOrWhiteSpace(m_SourcePath)) return;
            try
            {
                if (!resourcesAreSetUp) throw new InvalidOperationException("Shader resources are missing or compute shaders are unsupported");
                EnsureMaterials();
                EnsureSorterAndRegister();
                m_LoadOperation = new GaussianSplatLoadOperation(Application.streamingAssetsPath, m_SourcePath);
                isLoading = true;
#if UNITY_EDITOR
                UnityEditor.EditorApplication.update += EditorUpdateLoading;
#endif
            }
            catch (Exception e) { LoadFailed(e); }
        }

        void LoadFailed(Exception e)
        {
            DisposeResources();
            loadError = e.Message;
            Debug.LogError($"Cannot load splats '{m_SourcePath}': {e.Message}", this);
            FinishLoading();
        }

        void FinishLoading()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= EditorUpdateLoading;
#endif
            m_LoadOperation?.Dispose();
            m_LoadOperation = null;
            isLoading = false;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
            UnityEditor.SceneView.RepaintAll();
#endif
        }

        void SetDataOnCS(CommandBuffer cmb, KernelIndices kernel)
        {
            ComputeShader cs = m_CSSplatUtilities;
            int kernelIndex = (int) kernel;
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatPos, m_GpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatOther, m_GpuOtherData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSH, m_GpuSHData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatColor, m_GpuColorData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatViewData, m_GpuView);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.OrderBuffer, m_GpuSortKeys);

            cmb.SetComputeIntParam(cs, Props.SHCoefficients, m_SHCoefficients);
            cmb.SetComputeIntParam(cs, Props.SplatCount, m_SplatCount);
        }

        internal void SetDataOnMaterial(MaterialPropertyBlock mat)
        {
            mat.SetBuffer(Props.SplatPos, m_GpuPosData);
            mat.SetBuffer(Props.SplatOther, m_GpuOtherData);
            mat.SetBuffer(Props.SplatSH, m_GpuSHData);
            mat.SetBuffer(Props.SplatColor, m_GpuColorData);
            mat.SetInteger(Props.SHCoefficients, m_SHCoefficients);
            mat.SetInteger(Props.SplatCount, m_SplatCount);
        }

        static void DisposeBuffer(ref GraphicsBuffer buf)
        {
            buf?.Dispose();
            buf = null;
        }

        void DisposeResources()
        {
            DisposeBuffer(ref m_GpuColorData);

            DisposeBuffer(ref m_GpuPosData);
            DisposeBuffer(ref m_GpuOtherData);
            DisposeBuffer(ref m_GpuSHData);

            DisposeBuffer(ref m_GpuView);
            DisposeBuffer(ref m_GpuIndexBuffer);
            DisposeBuffer(ref m_GpuSortDistances);
            DisposeBuffer(ref m_GpuSortKeys);

            m_SorterArgs.resources.Dispose();

            m_SplatCount = 0;
        }

        public void OnDisable()
        {
            FinishLoading();
            DisposeResources();
            GaussianSplatRenderSystem.instance.UnregisterSplat(this);
            m_Registered = false;

            DestroyImmediate(m_MatSplats);
            DestroyImmediate(m_MatComposite);
            DestroyImmediate(m_MatDebugPoints);
            DestroyImmediate(m_MatDebugBoxes);
        }

        internal void CalcViewData(CommandBuffer cmb, Camera cam)
        {
            if (cam.cameraType == CameraType.Preview)
                return;

            var tr = transform;

            Matrix4x4 matView = cam.worldToCameraMatrix;
            Matrix4x4 matO2W = tr.localToWorldMatrix;
            Matrix4x4 matW2O = tr.worldToLocalMatrix;
            int screenW = cam.pixelWidth, screenH = cam.pixelHeight;
            int eyeW = XRSettings.eyeTextureWidth, eyeH = XRSettings.eyeTextureHeight;
            Vector4 screenPar = new Vector4(eyeW != 0 ? eyeW : screenW, eyeH != 0 ? eyeH : screenH, 0, 0);
            Vector4 camPos = cam.transform.position;

            // calculate view dependent data for each splat
            SetDataOnCS(cmb, KernelIndices.CalcViewData);

            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, matView * matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, matW2O);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecWorldSpaceCameraPos, camPos);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.SplatScale, m_SplatScale);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.SplatOpacityScale, m_OpacityScale);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SHOrder, m_SHOrder);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SHOnly, m_SHOnly ? 1 : 0);

            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.CalcViewData, out uint gsX, out _, out _);
            cmb.DispatchCompute(m_CSSplatUtilities, (int)KernelIndices.CalcViewData, (m_GpuView.count + (int)gsX - 1)/(int)gsX, 1, 1);
        }

        internal void SortPoints(CommandBuffer cmd, Camera cam, Matrix4x4 matrix)
        {
            if (cam.cameraType == CameraType.Preview)
                return;

            Matrix4x4 worldToCamMatrix = cam.worldToCameraMatrix;
            worldToCamMatrix.m20 *= -1;
            worldToCamMatrix.m21 *= -1;
            worldToCamMatrix.m22 *= -1;

            // calculate distance to the camera for each splat
            cmd.BeginSample(s_ProfSort);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatSortDistances, m_GpuSortDistances);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatSortKeys, m_GpuSortKeys);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatPos, m_GpuPosData);
            cmd.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, worldToCamMatrix * matrix);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatCount, m_SplatCount);
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.CalcDistances, out uint gsX, out _, out _);
            cmd.DispatchCompute(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, (m_GpuSortDistances.count + (int)gsX - 1)/(int)gsX, 1, 1);

            // sort the splats
            EnsureSorterAndRegister();
            m_Sorter.Dispatch(cmd, m_SorterArgs);
            cmd.EndSample(s_ProfSort);
        }

        public void Update()
        {
            if (m_PrevSourcePath != m_SourcePath) Reload();
#if UNITY_EDITOR
            if (!Application.isPlaying) return;
#endif
            UpdateLoading();
        }

#if UNITY_EDITOR
        void EditorUpdateLoading()
        {
            if (!Application.isPlaying) UpdateLoading();
        }
#endif

        void UpdateLoading()
        {
            if (m_LoadOperation == null) return;
            try
            {
                m_LoadOperation.Update();
                using var data = m_LoadOperation.TakeData();
                if (data != null)
                {
                    CreateResources(data);
#if UNITY_EDITOR
                    UnityEditor.SceneView.RepaintAll();
#endif
                }
                if (m_LoadOperation.isDone)
                {
                    cameras = m_LoadOperation.cameras;
                    if (m_LoadOperation.cameraError != null) Debug.LogWarning(m_LoadOperation.cameraError, this);
                    FinishLoading();
                }
            }
            catch (Exception e) { LoadFailed(e); }
        }

        public void ActivateCamera(int index)
        {
            Camera mainCam = Camera.main;
            if (!mainCam)
                return;
            if (cameras == null || index < 0 || index >= cameras.Length)
                return;

            var selfTr = transform;
            var camTr = mainCam.transform;
            var prevParent = camTr.parent;
            var cam = cameras[index];
            camTr.parent = selfTr;
            camTr.localPosition = cam.position;
            camTr.localRotation = cam.rotation;
            camTr.parent = prevParent;
            camTr.localScale = Vector3.one;
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(camTr);
#endif
        }
    }
}
