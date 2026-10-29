// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
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
        readonly RenderTargetIdentifier[] m_ReprojectionTargets =
        {
            new(GaussianSplatRenderer.Props.GaussianSplatRT),
            new(GaussianSplatRenderer.Props.GaussianSplatDepthRT)
        };

        CommandBuffer m_CommandBuffer;
        GraphicsBuffer m_QuadIndexBuffer;
        GraphicsBuffer m_GlobalUniforms;
        Material m_MatSplats, m_MatComposite, m_MatDebugPoints;
        uint m_FrameOffset;
        GraphicsBuffer m_GpuView, m_GpuSortDistances, m_GpuSortKeys;
        GpuSorting m_Sorter;
        GpuSorting.Args m_SorterArgs;
        readonly MaterialPropertyBlock m_DrawProperties = new();
        int m_SplatCount;
        string m_BufferError;
        const int kGpuViewDataSize = 40;
        GaussianSplatTemporalAccumulator m_TemporalAccumulator;
        int m_SceneVersion;
#if UNITY_EDITOR
        readonly HashSet<Camera> m_RenderedSceneViews = new();
#endif

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Size = 16)]
        struct SplatGlobalUniforms // match cbuffer SplatGlobalUniforms in shaders
        {
            // ReSharper disable once NotAccessedField.Local - used on shader side
            public uint transparencyMode;
            // ReSharper disable once NotAccessedField.Local - used on shader side
            public uint frameOffset;
        }

        public void RegisterSplat(GaussianSplatRenderer r)
        {
            if (m_Splats.Count == 0)
            {
                if (GraphicsSettings.currentRenderPipeline == null)
                    Camera.onPreCull += OnPreCullCamera;
#if UNITY_EDITOR
                UnityEditor.ObjectChangeEvents.changesPublished += OnSceneChanged;
                UnityEditor.SceneView.beforeSceneGui += OnBeforeSceneGUI;
#endif
            }

            m_Splats.Add(r, new MaterialPropertyBlock());
            InvalidateTemporalHistory();
        }

        public void UnregisterSplat(GaussianSplatRenderer r)
        {
            if (!m_Splats.ContainsKey(r))
                return;
            m_Splats.Remove(r);
            InvalidateTemporalHistory();
            if (m_Splats.Count == 0)
                CleanupAfterAllSplatsDeleted();
        }

        void CleanupAfterAllSplatsDeleted()
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
            DisposeSplatBuffers();
            m_Sorter = null;
            m_QuadIndexBuffer?.Dispose();
            m_QuadIndexBuffer = null;
            m_CommandBuffer?.Dispose();
            m_CommandBuffer = null;
            m_GlobalUniforms?.Dispose();
            m_GlobalUniforms = null;
            UnityEngine.Object.DestroyImmediate(m_MatSplats);
            UnityEngine.Object.DestroyImmediate(m_MatComposite);
            UnityEngine.Object.DestroyImmediate(m_MatDebugPoints);
            Camera.onPreCull -= OnPreCullCamera;
            m_TemporalAccumulator?.Dispose();
            m_TemporalAccumulator = null;
#if UNITY_EDITOR
            UnityEditor.ObjectChangeEvents.changesPublished -= OnSceneChanged;
            UnityEditor.SceneView.beforeSceneGui -= OnBeforeSceneGUI;
            m_RenderedSceneViews.Clear();
#endif
        }

        public void InvalidateTemporalHistory() => ++m_SceneVersion;

        void DisposeSplatBuffers()
        {
            m_GpuView?.Dispose(); m_GpuView = null;
            m_GpuSortDistances?.Dispose(); m_GpuSortDistances = null;
            m_GpuSortKeys?.Dispose(); m_GpuSortKeys = null;
            m_SorterArgs.resources.Dispose();
            m_SorterArgs = default;
        }

        void EnsureSplatBuffers()
        {
            if (m_GpuView != null && m_GpuView.count == m_SplatCount)
                return;
            DisposeSplatBuffers();
            try
            {
                m_GpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SplatCount, kGpuViewDataSize) {name = "GaussianSplatViewData"};
                m_GpuSortDistances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SplatCount, 4) {name = "GaussianSplatSortDistances"};
                m_GpuSortKeys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SplatCount, 4) {name = "GaussianSplatSortIndices"};
                m_Sorter ??= new GpuSorting(GaussianSplatRenderOptions.instance.csUtilities);
                m_SorterArgs = new GpuSorting.Args
                {
                    count = (uint)m_SplatCount,
                    inputKeys = m_GpuSortDistances,
                    inputValues = m_GpuSortKeys,
                    resources = GpuSorting.SupportResources.Load((uint)m_SplatCount)
                };
            }
            catch
            {
                DisposeSplatBuffers();
                throw;
            }
        }

#if UNITY_EDITOR
        // Invalidate temporal history upon scene edits. Ignore camera changes,
        // since e.g. reprojected temporal filtering can handle camera movement; for
        // non-reprojected case this will be handled elsewhere.
        void OnSceneChanged(ref UnityEditor.ObjectChangeEventStream stream)
        {
            for (int i = 0; i < stream.length; ++i)
            {
                if (stream.GetEventType(i) == UnityEditor.ObjectChangeKind.ChangeGameObjectOrComponentProperties)
                {
                    stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var change);
                    var obj = UnityEditor.EditorUtility.InstanceIDToObject(change.instanceId);
                    if (obj is Camera || obj is Transform tr && tr.GetComponent<Camera>() != null)
                        continue;
                }
                InvalidateTemporalHistory();
                break;
            }
        }
        void OnBeforeSceneGUI(UnityEditor.SceneView view) => m_RenderedSceneViews.Remove(view.camera);
#endif

        int GetTemporalSceneHash()
        {
            var hash = new HashCode();
            hash.Add(m_SceneVersion);
            var options = GaussianSplatRenderOptions.instance;
            hash.Add(options.m_SHOnly);
            foreach (var (gs, _) in m_ActiveSplats)
            {
                hash.Add(gs.GetInstanceID());
                hash.Add(gs.transform.localToWorldMatrix);
                hash.Add(gs.m_SplatScale);
                hash.Add(gs.m_OpacityScale);
                hash.Add(gs.m_SHOrder);
            }
            return hash.ToHashCode();
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

            // points read source buffers directly; no need for shared view/sort buffers
            if (GaussianSplatRenderOptions.instance.m_RenderMode == RenderMode.DebugPoints)
                return true;

            long count = 0;
            string error = null;
            foreach (var (gs, _) in m_ActiveSplats)
            {
                count += gs.splatCount;
                
                // view preparation dispatch is 1D, with 1024 group size
                int splatLimit = 65535 * 1024;
                if (gs.splatCount > splatLimit)
                    error = $"Can't render '{gs.name}': view preparation supports at most {splatLimit:N0} splats per object";
            }

            // check view data buffer against maximum supported by system
            long bytes = count * kGpuViewDataSize;
            long limit = SystemInfo.maxGraphicsBufferSize;
            if (count > int.MaxValue || bytes > limit)
                error = $"Cannot render all {count:N0} splats: shared view buffer needs {bytes:N0} bytes (max graphics buffer size {limit:N0})";
            
            // radix sort dispatches one group per 3840 keys
            var options = GaussianSplatRenderOptions.instance;
            int sortLimit = 65535 * 3840;
            if (options.m_RenderMode == RenderMode.Splats && options.needSorting && count > sortLimit)
                error = $"Cannot sort all {count:N0} splats: we support at most {sortLimit:N0} splats";
            if (error != null)
            {
                if (m_BufferError != error) Debug.LogError(error);
                m_BufferError = error;
                return false;
            }
            m_BufferError = null;
            m_SplatCount = (int)count;
            EnsureSplatBuffers();

            return true;
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public void SortAllSplats(Camera cam, CommandBuffer cmb)
        {
            if (cam.cameraType == CameraType.Preview)
                return;
            if (!GaussianSplatRenderOptions.instance.needSorting)
                return;
            cmb.BeginSample(GaussianSplatRenderer.s_ProfSort);
            m_SorterArgs.count = (uint)m_SplatCount;
            m_Sorter.Dispatch(cmb, m_SorterArgs);
            cmb.EndSample(GaussianSplatRenderer.s_ProfSort);
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public void CacheViewDataForAllSplats(Camera cam, CommandBuffer cmb)
        {
            var options = GaussianSplatRenderOptions.instance;
            if (options.m_RenderMode == RenderMode.DebugPoints)
                return;
            int offset = 0;
            foreach (var (gs, _) in m_ActiveSplats)
            {
                cmb.BeginSample(s_ProfCalcView);
                gs.CalcViewData(cmb, cam, m_GpuView, m_GpuSortDistances, m_GpuSortKeys, offset, options.needSorting);
                cmb.EndSample(s_ProfCalcView);
                offset += gs.splatCount;
            }
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public void RenderAllSplats(Camera cam, CommandBuffer cmb)
        {
            EnsureMaterials();
            GaussianSplatRenderOptions settings = GaussianSplatRenderOptions.instance;
            if (m_MatSplats == null)
                return;

            EnsureQuadIndexBuffer();
            if (settings.m_RenderMode == RenderMode.DebugPoints)
            {
                RenderDebugPoints(cmb, settings);
                return;
            }

            bool stochastic = settings.m_Transparency == TransparencyMode.Stochastic;
            bool reprojected = stochastic && settings.m_RenderMode == RenderMode.Splats &&
                               settings.m_TemporalFilter == StochasticTemporalFilter.Reprojected &&
                               GraphicsSettings.currentRenderPipeline == null;
            if (reprojected) m_MatSplats.EnableKeyword("SPLAT_REPROJECTED");
            else m_MatSplats.DisableKeyword("SPLAT_REPROJECTED");

            m_GlobalUniforms ??= new GraphicsBuffer(GraphicsBuffer.Target.Constant, 1, UnsafeUtility.SizeOf<SplatGlobalUniforms>());
            NativeArray<SplatGlobalUniforms> sgu = new(1, Allocator.Temp);
            sgu[0] = new SplatGlobalUniforms
            {
                transparencyMode = (uint)settings.m_Transparency,
                frameOffset = m_FrameOffset
            };
            using (sgu)
                cmb.SetBufferData(m_GlobalUniforms, sgu);
            m_FrameOffset++;

            m_MatSplats.SetInt(GaussianSplatRenderer.Props.SrcBlend, (int)(stochastic ? BlendMode.One : BlendMode.OneMinusDstAlpha));
            m_MatSplats.SetInt(GaussianSplatRenderer.Props.DstBlend, (int)(stochastic ? BlendMode.Zero : BlendMode.One));
            m_MatSplats.SetInt(GaussianSplatRenderer.Props.ZWrite, stochastic ? 1 : 0);

            var mpb = m_DrawProperties;
            mpb.Clear();
            mpb.SetBuffer(GaussianSplatRenderer.Props.SplatViewData, m_GpuView);
            mpb.SetBuffer(GaussianSplatRenderer.Props.OrderBuffer, m_GpuSortKeys);
            mpb.SetConstantBuffer(GaussianSplatRenderer.Props.SplatGlobalUniforms, m_GlobalUniforms, 0, m_GlobalUniforms.stride);
            cmb.BeginSample(s_ProfDraw);
            cmb.DrawProcedural(m_QuadIndexBuffer, Matrix4x4.identity, m_MatSplats, 0, MeshTopology.Triangles, 6, m_SplatCount, mpb);
            cmb.EndSample(s_ProfDraw);
        }

        void RenderDebugPoints(CommandBuffer cmb, GaussianSplatRenderOptions settings)
        {
            foreach (var (gs, mpb) in m_ActiveSplats)
            {
                mpb.Clear();
                gs.SetDataOnMaterial(mpb);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatSize, settings.m_PointDisplaySize);
                cmb.BeginSample(s_ProfDraw);
                cmb.DrawProcedural(m_QuadIndexBuffer, gs.transform.localToWorldMatrix, m_MatDebugPoints, 0,
                    MeshTopology.Triangles, 6, gs.splatCount, mpb);
                cmb.EndSample(s_ProfDraw);
            }
        }

        public Material SortAndRenderSplats(Camera cam, CommandBuffer cmb)
        {
            CacheViewDataForAllSplats(cam, cmb);
            SortAllSplats(cam, cmb);
            RenderAllSplats(cam, cmb);
            return m_MatComposite;
        }

        void EnsureQuadIndexBuffer()
        {
            if (m_QuadIndexBuffer != null)
                return;
            m_QuadIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, 6, 2);
            m_QuadIndexBuffer.SetData(new ushort[] {0, 1, 2, 1, 3, 2});
        }

        void EnsureMaterials()
        {
            GaussianSplatRenderOptions settings = GaussianSplatRenderOptions.instance;
            if (m_MatSplats == null && settings.resourcesFound)
            {
                m_MatSplats = new Material(settings.shaderSplats) {name = "GaussianSplats"};
                m_MatComposite = new Material(settings.shaderComposite) {name = "GaussianClearDstAlpha"};
                m_MatDebugPoints = new Material(settings.shaderDebugPoints) {name = "GaussianDebugPoints"};
            }
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
#if UNITY_EDITOR
            // camera callbacks are also invoked for outline passes etc.; only advance
            // temporal history once per actual repaint
            if (cam.cameraType == CameraType.SceneView &&
                GaussianSplatRenderOptions.instance.m_TemporalFilter != StochasticTemporalFilter.None &&
                !m_RenderedSceneViews.Add(cam))
                return;
#endif
            if (!GatherSplatsForCamera(cam))
            {
                m_CommandBuffer?.Clear();
                m_TemporalAccumulator?.Reset(cam);
                return;
            }

            InitialClearCmdBuffer(cam);

            var options = GaussianSplatRenderOptions.instance;
            bool temporal = options.m_TemporalFilter != StochasticTemporalFilter.None &&
                            options.m_Transparency == TransparencyMode.Stochastic && options.m_RenderMode == RenderMode.Splats;
            bool reprojected = temporal && options.m_TemporalFilter == StochasticTemporalFilter.Reprojected;
            m_CommandBuffer.GetTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT, -1, -1, 0, FilterMode.Point, GraphicsFormat.R16G16B16A16_SFloat);
            if (reprojected)
            {
                // reprojection also writes splat's depth into another MRT target
                m_CommandBuffer.GetTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatDepthRT, -1, -1, 0, FilterMode.Point, GraphicsFormat.R32_SFloat);
                m_CommandBuffer.SetRenderTarget(m_ReprojectionTargets, BuiltinRenderTextureType.CurrentActive);
            }
            else
                m_CommandBuffer.SetRenderTarget(GaussianSplatRenderer.Props.GaussianSplatRT, BuiltinRenderTextureType.CurrentActive);
            m_CommandBuffer.ClearRenderTarget(RTClearFlags.Color, new Color(0, 0, 0, 0), 0, 0);

            // We only need this to determine whether we're rendering into backbuffer or not. However, detection this
            // way only works in BiRP so only do it here.
            m_CommandBuffer.SetGlobalTexture(GaussianSplatRenderer.Props.CameraTargetTexture, BuiltinRenderTextureType.CameraTarget);

            // Prepare per-object ranges, then globally sort and draw.
            Material matComposite = SortAndRenderSplats(cam, m_CommandBuffer);

            // compose
            m_CommandBuffer.BeginSample(s_ProfCompose);
            // TODO: support temporal accumulation in URP/HDRP too.
            if (temporal)
            {
                m_TemporalAccumulator ??= new GaussianSplatTemporalAccumulator();
                m_TemporalAccumulator.Render(m_CommandBuffer, cam, matComposite, GetTemporalSceneHash(), options.m_TemporalFilter);
            }
            else
            {
                m_TemporalAccumulator?.Dispose();
                m_TemporalAccumulator = null;
                m_CommandBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                m_CommandBuffer.DrawProcedural(Matrix4x4.identity, matComposite, 0, MeshTopology.Triangles, 3, 1);
            }
            m_CommandBuffer.EndSample(s_ProfCompose);
            m_CommandBuffer.ReleaseTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT);
            if (reprojected)
                m_CommandBuffer.ReleaseTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatDepthRT);
        }
    }

    [ExecuteInEditMode]
    public class GaussianSplatRenderer : MonoBehaviour
    {
        [Tooltip("PLY/SPZ path relative to Assets/StreamingAssets") ]
        public string m_SourcePath;

        // Call after any runtime changes that affect splat visibility / history
        public void InvalidateTemporalHistory() => GaussianSplatRenderSystem.instance.InvalidateTemporalHistory();

        [Range(0.1f, 2.0f)] [Tooltip("Additional scaling factor for the splats")]
        public float m_SplatScale = 1.0f;
        [Range(0.05f, 20.0f)]
        [Tooltip("Additional scaling factor for opacity")]
        public float m_OpacityScale = 1.0f;
        [Range(0, 3)] [Tooltip("Spherical Harmonics order to use")]
        public int m_SHOrder = 3;
        int m_SplatCount;
        GraphicsBuffer m_GpuPosData;
        GraphicsBuffer m_GpuOtherData;
        GraphicsBuffer m_GpuSHData;
        GraphicsBuffer m_GpuColorData;
        string m_PrevSourcePath;
        GaussianSplatLoadOperation m_LoadOperation;
        int m_SHCoefficients;
        bool m_Registered;

        public bool isLoading { get; private set; }
        public string loadError { get; private set; }
        public Bounds localBounds { get; private set; }
        public int sourceSHDegree { get; private set; }
        public GaussianSplatCameras.CameraInfo[] cameras { get; private set; } = Array.Empty<GaussianSplatCameras.CameraInfo>();

        internal static readonly ProfilerMarker s_ProfSort = new(ProfilerCategory.Render, "GaussianSplat.Sort", MarkerFlags.SampleGPU);

        internal static class Props
        {
            public static readonly int SrcBlend = Shader.PropertyToID("_SrcBlend");
            public static readonly int DstBlend = Shader.PropertyToID("_DstBlend");
            public static readonly int ZWrite = Shader.PropertyToID("_ZWrite");
            public static readonly int SplatGlobalUniforms = Shader.PropertyToID("SplatGlobalUniforms");
            public static readonly int SplatPos = Shader.PropertyToID("_SplatPos");
            public static readonly int SplatOther = Shader.PropertyToID("_SplatOther");
            public static readonly int SplatSH = Shader.PropertyToID("_SplatSH");
            public static readonly int SplatColor = Shader.PropertyToID("_SplatColor");
            public static readonly int SHCoefficients = Shader.PropertyToID("_SHCoefficients");
            public static readonly int SplatViewData = Shader.PropertyToID("_SplatViewData");
            public static readonly int SplatOffset = Shader.PropertyToID("_SplatOffset");
            public static readonly int WriteSortKeys = Shader.PropertyToID("_WriteSortKeys");
            public static readonly int OrderBuffer = Shader.PropertyToID("_OrderBuffer");
            public static readonly int SplatScale = Shader.PropertyToID("_SplatScale");
            public static readonly int SplatOpacityScale = Shader.PropertyToID("_SplatOpacityScale");
            public static readonly int SplatSize = Shader.PropertyToID("_SplatSize");
            public static readonly int SplatCount = Shader.PropertyToID("_SplatCount");
            public static readonly int SHOrder = Shader.PropertyToID("_SHOrder");
            public static readonly int SHOnly = Shader.PropertyToID("_SHOnly");
            public static readonly int GaussianSplatRT = Shader.PropertyToID("_GaussianSplatRT");
            public static readonly int GaussianSplatDepthRT = Shader.PropertyToID("_GaussianSplatDepthRT");
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

        const int kKernelCalcViewData = 0;

        public bool HasValidRenderResources => resourcesAreSetUp;
        public bool HasValidRenderSetup => m_SplatCount > 0 && m_GpuPosData != null && m_GpuColorData != null;

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
            EnsureRegistered();
            GaussianSplatRenderSystem.instance.InvalidateTemporalHistory();
        }

        bool resourcesAreSetUp => GaussianSplatRenderOptions.instance.resourcesFound;

        public void EnsureRegistered()
        {
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
            if (!isActiveAndEnabled || string.IsNullOrWhiteSpace(m_SourcePath)) return;
            try
            {
                if (!resourcesAreSetUp) throw new InvalidOperationException("Shader resources are missing or compute shaders are unsupported");
                EnsureRegistered();
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

        void SetDataOnCS(CommandBuffer cmb)
        {
            ComputeShader cs = GaussianSplatRenderOptions.instance.csUtilities;
            int kernelIndex = kKernelCalcViewData;
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatPos, m_GpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatOther, m_GpuOtherData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSH, m_GpuSHData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatColor, m_GpuColorData);

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

            m_SplatCount = 0;
        }

        public void OnDisable()
        {
            FinishLoading();
            DisposeResources();
            GaussianSplatRenderSystem.instance.UnregisterSplat(this);
            m_Registered = false;

        }

        internal void CalcViewData(CommandBuffer cmb, Camera cam, GraphicsBuffer view, GraphicsBuffer distances,
            GraphicsBuffer indices, int offset, bool writeSortKeys)
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
            SetDataOnCS(cmb);

            GaussianSplatRenderOptions options = GaussianSplatRenderOptions.instance;
            ComputeShader cs = options.csUtilities;
            cmb.SetComputeBufferParam(cs, kKernelCalcViewData, Props.SplatViewData, view);
            cmb.SetComputeBufferParam(cs, kKernelCalcViewData, Props.SplatSortDistances, distances);
            cmb.SetComputeBufferParam(cs, kKernelCalcViewData, Props.SplatSortKeys, indices);
            cmb.SetComputeIntParam(cs, Props.SplatOffset, offset);
            cmb.SetComputeIntParam(cs, Props.WriteSortKeys, writeSortKeys ? 1 : 0);
            cmb.SetComputeMatrixParam(cs, Props.MatrixMV, matView * matO2W);
            cmb.SetComputeMatrixParam(cs, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(cs, Props.MatrixWorldToObject, matW2O);

            cmb.SetComputeVectorParam(cs, Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(cs, Props.VecWorldSpaceCameraPos, camPos);
            cmb.SetComputeFloatParam(cs, Props.SplatScale, m_SplatScale);
            cmb.SetComputeFloatParam(cs, Props.SplatOpacityScale, m_OpacityScale);
            cmb.SetComputeIntParam(cs, Props.SHOrder, m_SHOrder);
            cmb.SetComputeIntParam(cs, Props.SHOnly, options.m_SHOnly ? 1 : 0);

            cs.GetKernelThreadGroupSizes(kKernelCalcViewData, out uint gsX, out _, out _);
            cmb.DispatchCompute(cs, kKernelCalcViewData, (m_SplatCount + (int)gsX - 1)/(int)gsX, 1, 1);
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
