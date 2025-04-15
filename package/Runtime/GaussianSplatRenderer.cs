// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.XR;
using Object = UnityEngine.Object;

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
        GraphicsBuffer m_CubeIndexBuffer;
        GraphicsBuffer m_GlobalUniforms;
        Material m_MatSplats;
        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        internal Material m_MatComposite;
        Material m_MatDebugPoints;
        Material m_MatDebugBoxes;
        uint m_FrameOffset;
        GaussianSplatTemporalFilter m_TemporalFilter;

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
            }

            m_Splats.Add(r, new MaterialPropertyBlock());
        }

        public void UnregisterSplat(GaussianSplatRenderer r)
        {
            if (!m_Splats.ContainsKey(r))
                return;
            m_Splats.Remove(r);
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
            m_CubeIndexBuffer?.Dispose();
            m_CubeIndexBuffer = null;
            m_CommandBuffer?.Dispose();
            m_CommandBuffer = null;
            m_GlobalUniforms?.Dispose();
            m_GlobalUniforms = null;
            Object.DestroyImmediate(m_MatSplats);
            Object.DestroyImmediate(m_MatComposite);
            Object.DestroyImmediate(m_MatDebugPoints);
            Object.DestroyImmediate(m_MatDebugBoxes);
            m_TemporalFilter?.Dispose();
            m_TemporalFilter = null;
            Camera.onPreCull -= OnPreCullCamera;
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
                if (gs == null || !gs.isActiveAndEnabled || !gs.HasValidAsset || !gs.HasValidRenderSetup)
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
        public void SortAllSplats(Camera cam, CommandBuffer cmb)
        {
            if (cam.cameraType == CameraType.Preview)
                return;
            GaussianSplatSettings settings = GaussianSplatSettings.instance;
            if (!settings.needSorting)
                return; // no need to sort

            foreach (var kvp in m_ActiveSplats)
            {
                var gs = kvp.Item1;
                var matrix = gs.transform.localToWorldMatrix;
                if (gs.m_FrameCounter % settings.m_SortNthFrame == 0)
                    gs.SortPoints(cmb, cam, matrix);
                ++gs.m_FrameCounter;
            }
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public void CacheViewDataForAllSplats(Camera cam, CommandBuffer cmb)
        {
            foreach (var kvp in m_ActiveSplats)
            {
                var gs = kvp.Item1;
                cmb.BeginSample(s_ProfCalcView);
                gs.CalcViewData(cmb, cam);
                cmb.EndSample(s_ProfCalcView);
            }
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public void RenderAllSplats(Camera cam, CommandBuffer cmb)
        {
            EnsureMaterials();
            GaussianSplatSettings settings = GaussianSplatSettings.instance;
            Material displayMat = settings.m_RenderMode switch
            {
                DebugRenderMode.DebugPoints => m_MatDebugPoints,
                DebugRenderMode.DebugPointIndices => m_MatDebugPoints,
                DebugRenderMode.DebugBoxes => m_MatDebugBoxes,
                DebugRenderMode.DebugChunkBounds => m_MatDebugBoxes,
                _ => m_MatSplats
            };
            if (displayMat == null)
                return;

            EnsureCubeIndexBuffer();

            m_GlobalUniforms ??= new GraphicsBuffer(GraphicsBuffer.Target.Constant, 1, UnsafeUtility.SizeOf<SplatGlobalUniforms>());
            NativeArray<SplatGlobalUniforms> sgu = new(1, Allocator.Temp);
            sgu[0] = new SplatGlobalUniforms { transparencyMode = (uint)settings.m_Transparency, frameOffset = m_FrameOffset };
            cmb.SetBufferData(m_GlobalUniforms, sgu);
            m_FrameOffset++;

            bool stochastic = !settings.isDebugRender && settings.m_Transparency == TransparencyMode.Stochastic;
            displayMat.SetInt(GaussianSplatRenderer.Props.SrcBlend, (int)(stochastic ? BlendMode.One : BlendMode.OneMinusDstAlpha));
            displayMat.SetInt(GaussianSplatRenderer.Props.DstBlend, (int)(stochastic ? BlendMode.Zero : BlendMode.One));
            displayMat.SetInt(GaussianSplatRenderer.Props.ZWrite, stochastic ? 1 : 0);

            foreach (var kvp in m_ActiveSplats)
            {
                var gs = kvp.Item1;

                var matrix = gs.transform.localToWorldMatrix;

                var mpb = kvp.Item2;
                mpb.Clear();

                gs.SetAssetDataOnMaterial(mpb);

                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatChunks, gs.m_GpuChunks);

                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatViewData, gs.m_GpuView);

                mpb.SetBuffer(GaussianSplatRenderer.Props.OrderBuffer, gs.m_GpuSortKeys);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatScale, gs.m_SplatScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatOpacityScale, gs.m_OpacityScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatSize, settings.m_PointDisplaySize);
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOrder, gs.m_SHOrder);
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOnly, settings.m_SHOnly ? 1 : 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayIndex, settings.m_RenderMode == DebugRenderMode.DebugPointIndices ? 1 : 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayChunks, settings.m_RenderMode == DebugRenderMode.DebugChunkBounds ? 1 : 0);
                mpb.SetConstantBuffer(GaussianSplatRenderer.Props.SplatGlobalUniforms, m_GlobalUniforms, 0, m_GlobalUniforms.stride);

                int indexCount = 6;
                int instanceCount = gs.splatCount;
                MeshTopology topology = MeshTopology.Triangles;
                if (settings.m_RenderMode is DebugRenderMode.DebugBoxes or DebugRenderMode.DebugChunkBounds)
                    indexCount = 36;
                if (settings.m_RenderMode == DebugRenderMode.DebugChunkBounds)
                    instanceCount = gs.m_GpuChunksValid ? gs.m_GpuChunks.count : 0;

                cmb.BeginSample(s_ProfDraw);
                cmb.DrawProcedural(m_CubeIndexBuffer, matrix, displayMat, 0, topology, indexCount, instanceCount, mpb);
                cmb.EndSample(s_ProfDraw);
            }
        }

        // cube indices, most often we use only the first quad
        void EnsureCubeIndexBuffer()
        {
            if (m_CubeIndexBuffer != null)
                return;
            m_CubeIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, 36, 2);
            m_CubeIndexBuffer.SetData(new ushort[]
            {
                0, 1, 2, 1, 3, 2,
                4, 6, 5, 5, 6, 7,
                0, 2, 4, 4, 2, 6,
                1, 5, 3, 5, 7, 3,
                0, 4, 1, 4, 5, 1,
                2, 3, 6, 3, 7, 6
            });
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        internal void EnsureMaterials()
        {
            GaussianSplatSettings settings = GaussianSplatSettings.instance;
            if (m_MatSplats == null && settings.resourcesFound)
            {
                m_MatSplats = new Material(settings.shaderSplats) {name = "GaussianSplats"};
                m_MatComposite = new Material(settings.shaderComposite) {name = "GaussianClearDstAlpha"};
                m_MatDebugPoints = new Material(settings.shaderDebugPoints) {name = "GaussianDebugPoints"};
                m_MatDebugBoxes = new Material(settings.shaderDebugBoxes) {name = "GaussianDebugBoxes"};
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
            if (!GatherSplatsForCamera(cam))
                return;

            EnsureMaterials();
            var matComposite = m_MatComposite;
            if (!matComposite)
                return;

            InitialClearCmdBuffer(cam);

            // We only need this to determine whether we're rendering into backbuffer or not. However, detection this
            // way only works in BiRP so only do it here.
            m_CommandBuffer.SetGlobalTexture(GaussianSplatRenderer.Props.CameraTargetTexture,
                BuiltinRenderTextureType.CameraTarget);

            GaussianSplatSettings settings = GaussianSplatSettings.instance;
            if (!settings.isDebugRender) // Debug visualizations modes just render directly onto screen
            {
                m_CommandBuffer.GetTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT, -1, -1, 0, FilterMode.Point, GraphicsFormat.R16G16B16A16_SFloat);
                m_CommandBuffer.SetRenderTarget(GaussianSplatRenderer.Props.GaussianSplatRT, BuiltinRenderTextureType.CurrentActive);
                m_CommandBuffer.ClearRenderTarget(RTClearFlags.Color, new Color(0, 0, 0, 0), 0, 0);
            }

            // add sorting, view calc and drawing commands for all splat objects
            SortAllSplats(cam, m_CommandBuffer);
            CacheViewDataForAllSplats(cam, m_CommandBuffer);
            RenderAllSplats(cam, m_CommandBuffer);

            // compose
            if (!settings.isDebugRender)
            {
                m_CommandBuffer.BeginSample(s_ProfCompose);
                if (settings.m_Transparency == TransparencyMode.Stochastic && settings.m_TemporalFilter != TemporalFilter.None)
                {
                    m_TemporalFilter ??= new GaussianSplatTemporalFilter();
                    m_TemporalFilter.Render(m_CommandBuffer, cam, matComposite, 1,
                        GaussianSplatRenderer.Props.GaussianSplatRT,
                        BuiltinRenderTextureType.CameraTarget,
                        settings.m_FrameInfluence,
                        settings.m_VarianceClampScale);
                }
                else
                {
                    m_CommandBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                    m_CommandBuffer.DrawProcedural(Matrix4x4.identity, matComposite, 0, MeshTopology.Triangles, 3, 1);
                }
                m_CommandBuffer.EndSample(s_ProfCompose);
                m_CommandBuffer.ReleaseTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT);
            }
        }
    }

    [ExecuteInEditMode]
    public class GaussianSplatRenderer : MonoBehaviour
    {
        public GaussianSplatAsset m_Asset;

        [Tooltip("Rendering order compared to other splats. Within same order splats are sorted by distance. Higher order splats render 'on top of' lower order splats.")]
        public int m_RenderOrder;
        [Range(0.1f, 2.0f)] [Tooltip("Additional scaling factor for the splats")]
        public float m_SplatScale = 1.0f;
        [Range(0.05f, 20.0f)]
        [Tooltip("Additional scaling factor for opacity")]
        public float m_OpacityScale = 1.0f;
        [Range(0, 3)] [Tooltip("Spherical Harmonics order to use")]
        public int m_SHOrder = 3;

        public GaussianCutout[] m_Cutouts;

        int m_SplatCount; // initially same as asset splat count, but editing can change this
        GraphicsBuffer m_GpuSortDistances;
        internal GraphicsBuffer m_GpuSortKeys;
        GraphicsBuffer m_GpuPosData;
        GraphicsBuffer m_GpuOtherData;
        GraphicsBuffer m_GpuSHData;
        Texture m_GpuColorData;
        internal GraphicsBuffer m_GpuChunks;
        internal bool m_GpuChunksValid;
        internal GraphicsBuffer m_GpuView;

        // these buffers are only for splat editing, and are lazily created
        GraphicsBuffer m_GpuEditCutouts;
        GraphicsBuffer m_GpuEditCountsBounds;
        GraphicsBuffer m_GpuEditSelected;
        GraphicsBuffer m_GpuEditDeleted;
        GraphicsBuffer m_GpuEditSelectedMouseDown; // selection state at start of operation
        GraphicsBuffer m_GpuEditPosMouseDown; // position state at start of operation
        GraphicsBuffer m_GpuEditOtherMouseDown; // rotation/scale state at start of operation

        GpuSorting m_Sorter;
        GpuSorting.Args m_SorterArgs;

        internal int m_FrameCounter;
        GaussianSplatAsset m_PrevAsset;
        Hash128 m_PrevHash;
        bool m_Registered;

        static readonly ProfilerMarker s_ProfSort = new(ProfilerCategory.Render, "GaussianSplat.Sort", MarkerFlags.SampleGPU);

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
            public static readonly int SplatSelectedBits = Shader.PropertyToID("_SplatSelectedBits");
            public static readonly int SplatDeletedBits = Shader.PropertyToID("_SplatDeletedBits");
            public static readonly int SplatBitsValid = Shader.PropertyToID("_SplatBitsValid");
            public static readonly int SplatFormat = Shader.PropertyToID("_SplatFormat");
            public static readonly int SplatChunks = Shader.PropertyToID("_SplatChunks");
            public static readonly int SplatChunkCount = Shader.PropertyToID("_SplatChunkCount");
            public static readonly int SplatViewData = Shader.PropertyToID("_SplatViewData");
            public static readonly int OrderBuffer = Shader.PropertyToID("_OrderBuffer");
            public static readonly int SplatScale = Shader.PropertyToID("_SplatScale");
            public static readonly int SplatOpacityScale = Shader.PropertyToID("_SplatOpacityScale");
            public static readonly int SplatSize = Shader.PropertyToID("_SplatSize");
            public static readonly int SplatCount = Shader.PropertyToID("_SplatCount");
            public static readonly int SHOrder = Shader.PropertyToID("_SHOrder");
            public static readonly int SHOnly = Shader.PropertyToID("_SHOnly");
            public static readonly int DisplayIndex = Shader.PropertyToID("_DisplayIndex");
            public static readonly int DisplayChunks = Shader.PropertyToID("_DisplayChunks");
            public static readonly int GaussianSplatRT = Shader.PropertyToID("_GaussianSplatRT");
            public static readonly int SplatSortKeys = Shader.PropertyToID("_SplatSortKeys");
            public static readonly int SplatSortDistances = Shader.PropertyToID("_SplatSortDistances");
            public static readonly int SrcBuffer = Shader.PropertyToID("_SrcBuffer");
            public static readonly int DstBuffer = Shader.PropertyToID("_DstBuffer");
            public static readonly int BufferSize = Shader.PropertyToID("_BufferSize");
            public static readonly int MatrixMV = Shader.PropertyToID("_MatrixMV");
            public static readonly int MatrixObjectToWorld = Shader.PropertyToID("_MatrixObjectToWorld");
            public static readonly int MatrixWorldToObject = Shader.PropertyToID("_MatrixWorldToObject");
            public static readonly int VecScreenParams = Shader.PropertyToID("_VecScreenParams");
            public static readonly int VecWorldSpaceCameraPos = Shader.PropertyToID("_VecWorldSpaceCameraPos");
            public static readonly int CameraTargetTexture = Shader.PropertyToID("_CameraTargetTexture");
            public static readonly int SelectionCenter = Shader.PropertyToID("_SelectionCenter");
            public static readonly int SelectionDelta = Shader.PropertyToID("_SelectionDelta");
            public static readonly int SelectionDeltaRot = Shader.PropertyToID("_SelectionDeltaRot");
            public static readonly int SplatCutoutsCount = Shader.PropertyToID("_SplatCutoutsCount");
            public static readonly int SplatCutouts = Shader.PropertyToID("_SplatCutouts");
            public static readonly int SelectionMode = Shader.PropertyToID("_SelectionMode");
            public static readonly int SplatPosMouseDown = Shader.PropertyToID("_SplatPosMouseDown");
            public static readonly int SplatOtherMouseDown = Shader.PropertyToID("_SplatOtherMouseDown");
        }

        [field: NonSerialized] public bool editModified { get; private set; }
        [field: NonSerialized] public uint editSelectedSplats { get; private set; }
        [field: NonSerialized] public uint editDeletedSplats { get; private set; }
        [field: NonSerialized] public uint editCutSplats { get; private set; }
        [field: NonSerialized] public Bounds editSelectedBounds { get; private set; }

        public GaussianSplatAsset asset => m_Asset;
        public int splatCount => m_SplatCount;

        enum KernelIndices
        {
            SetIndices,
            CalcDistances,
            CalcViewData,
            UpdateEditData,
            InitEditData,
            ClearBuffer,
            InvertSelection,
            SelectAll,
            OrBuffers,
            SelectionUpdate,
            TranslateSelection,
            RotateSelection,
            ScaleSelection,
            ExportData,
            CopySplats,
        }

        public bool HasValidAsset =>
            m_Asset != null &&
            m_Asset.splatCount > 0 &&
            m_Asset.formatVersion == GaussianSplatAsset.kCurrentVersion &&
            m_Asset.posData != null &&
            m_Asset.otherData != null &&
            m_Asset.shData != null &&
            m_Asset.colorData != null;
        public bool HasValidRenderSetup => m_GpuPosData != null && m_GpuOtherData != null && m_GpuChunks != null;

        const int kGpuViewDataSize = 40;

        void CreateResourcesForAsset()
        {
            if (!HasValidAsset)
                return;

            m_SplatCount = asset.splatCount;
            m_GpuPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int) (asset.posData.dataSize / 4), 4) { name = "GaussianPosData" };
            m_GpuPosData.SetData(asset.posData.GetData<uint>());
            m_GpuOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int) (asset.otherData.dataSize / 4), 4) { name = "GaussianOtherData" };
            m_GpuOtherData.SetData(asset.otherData.GetData<uint>());
            m_GpuSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, (int) (asset.shData.dataSize / 4), 4) { name = "GaussianSHData" };
            m_GpuSHData.SetData(asset.shData.GetData<uint>());
            var (texWidth, texHeight) = GaussianSplatAsset.CalcTextureSize(asset.splatCount);
            var texFormat = GaussianSplatAsset.ColorFormatToGraphics(asset.colorFormat);
            var tex = new Texture2D(texWidth, texHeight, texFormat, TextureCreationFlags.DontInitializePixels | TextureCreationFlags.IgnoreMipmapLimit | TextureCreationFlags.DontUploadUponCreate) { name = "GaussianColorData" };
            tex.SetPixelData(asset.colorData.GetData<byte>(), 0);
            tex.Apply(false, true);
            m_GpuColorData = tex;
            if (asset.chunkData != null && asset.chunkData.dataSize != 0)
            {
                m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    (int) (asset.chunkData.dataSize / UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()),
                    UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()) {name = "GaussianChunkData"};
                m_GpuChunks.SetData(asset.chunkData.GetData<GaussianSplatAsset.ChunkInfo>());
                m_GpuChunksValid = true;
            }
            else
            {
                // just a dummy chunk buffer
                m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1,
                    UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()) {name = "GaussianChunkData"};
                m_GpuChunksValid = false;
            }

            m_GpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_Asset.splatCount, kGpuViewDataSize);

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
            ComputeShader cs = GaussianSplatSettings.instance.csUtilities;
            cs.SetBuffer((int)KernelIndices.SetIndices, Props.SplatSortKeys, m_GpuSortKeys);
            cs.SetInt(Props.SplatCount, m_GpuSortDistances.count);
            cs.GetKernelThreadGroupSizes((int)KernelIndices.SetIndices, out uint gsX, out _, out _);
            cs.Dispatch((int)KernelIndices.SetIndices, (m_GpuSortDistances.count + (int)gsX - 1)/(int)gsX, 1, 1);

            m_SorterArgs.inputKeys = m_GpuSortDistances;
            m_SorterArgs.inputValues = m_GpuSortKeys;
            m_SorterArgs.count = (uint)count;
            if (m_Sorter.Valid)
                m_SorterArgs.resources = GpuSorting.SupportResources.Load((uint)count);
        }

        bool resourcesAreSetUp => GaussianSplatSettings.instance.resourcesFound;

        public void EnsureSorterAndRegister()
        {
            if (m_Sorter == null && resourcesAreSetUp)
            {
                m_Sorter = new GpuSorting(GaussianSplatSettings.instance.csUtilities);
            }

            if (!m_Registered && resourcesAreSetUp)
            {
                GaussianSplatRenderSystem.instance.RegisterSplat(this);
                m_Registered = true;
            }
        }

        public void OnEnable()
        {
            m_FrameCounter = 0;
            if (!resourcesAreSetUp)
                return;

            EnsureSorterAndRegister();
            CreateResourcesForAsset();
        }

        void SetAssetDataOnCS(CommandBuffer cmb, KernelIndices kernel)
        {
            ComputeShader cs = GaussianSplatSettings.instance.csUtilities;
            int kernelIndex = (int) kernel;
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatPos, m_GpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatChunks, m_GpuChunks);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatOther, m_GpuOtherData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSH, m_GpuSHData);
            cmb.SetComputeTextureParam(cs, kernelIndex, Props.SplatColor, m_GpuColorData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSelectedBits, m_GpuEditSelected ?? m_GpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatDeletedBits, m_GpuEditDeleted ?? m_GpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatViewData, m_GpuView);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.OrderBuffer, m_GpuSortKeys);

            cmb.SetComputeIntParam(cs, Props.SplatBitsValid, m_GpuEditSelected != null && m_GpuEditDeleted != null ? 1 : 0);
            uint format = (uint)m_Asset.posFormat | ((uint)m_Asset.scaleFormat << 8) | ((uint)m_Asset.shFormat << 16);
            cmb.SetComputeIntParam(cs, Props.SplatFormat, (int)format);
            cmb.SetComputeIntParam(cs, Props.SplatCount, m_SplatCount);
            cmb.SetComputeIntParam(cs, Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);

            UpdateCutoutsBuffer();
            cmb.SetComputeIntParam(cs, Props.SplatCutoutsCount, m_Cutouts?.Length ?? 0);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatCutouts, m_GpuEditCutouts);
        }

        internal void SetAssetDataOnMaterial(MaterialPropertyBlock mat)
        {
            mat.SetBuffer(Props.SplatPos, m_GpuPosData);
            mat.SetBuffer(Props.SplatOther, m_GpuOtherData);
            mat.SetBuffer(Props.SplatSH, m_GpuSHData);
            mat.SetTexture(Props.SplatColor, m_GpuColorData);
            mat.SetBuffer(Props.SplatSelectedBits, m_GpuEditSelected ?? m_GpuPosData);
            mat.SetBuffer(Props.SplatDeletedBits, m_GpuEditDeleted ?? m_GpuPosData);
            mat.SetInt(Props.SplatBitsValid, m_GpuEditSelected != null && m_GpuEditDeleted != null ? 1 : 0);
            uint format = (uint)m_Asset.posFormat | ((uint)m_Asset.scaleFormat << 8) | ((uint)m_Asset.shFormat << 16);
            mat.SetInteger(Props.SplatFormat, (int)format);
            mat.SetInteger(Props.SplatCount, m_SplatCount);
            mat.SetInteger(Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);
        }

        static void DisposeBuffer(ref GraphicsBuffer buf)
        {
            buf?.Dispose();
            buf = null;
        }

        void DisposeResourcesForAsset()
        {
            DestroyImmediate(m_GpuColorData);

            DisposeBuffer(ref m_GpuPosData);
            DisposeBuffer(ref m_GpuOtherData);
            DisposeBuffer(ref m_GpuSHData);
            DisposeBuffer(ref m_GpuChunks);

            DisposeBuffer(ref m_GpuView);
            DisposeBuffer(ref m_GpuSortDistances);
            DisposeBuffer(ref m_GpuSortKeys);

            DisposeBuffer(ref m_GpuEditSelectedMouseDown);
            DisposeBuffer(ref m_GpuEditPosMouseDown);
            DisposeBuffer(ref m_GpuEditOtherMouseDown);
            DisposeBuffer(ref m_GpuEditSelected);
            DisposeBuffer(ref m_GpuEditDeleted);
            DisposeBuffer(ref m_GpuEditCountsBounds);
            DisposeBuffer(ref m_GpuEditCutouts);

            m_SorterArgs.resources.Dispose();

            m_SplatCount = 0;
            m_GpuChunksValid = false;

            editSelectedSplats = 0;
            editDeletedSplats = 0;
            editCutSplats = 0;
            editModified = false;
            editSelectedBounds = default;
        }

        public void OnDisable()
        {
            DisposeResourcesForAsset();
            GaussianSplatRenderSystem.instance.UnregisterSplat(this);
            m_Registered = false;
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
            SetAssetDataOnCS(cmb, KernelIndices.CalcViewData);

            GaussianSplatSettings settings = GaussianSplatSettings.instance;
            ComputeShader cs = settings.csUtilities;
            cmb.SetComputeMatrixParam(cs, Props.MatrixMV, matView * matO2W);
            cmb.SetComputeMatrixParam(cs, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(cs, Props.MatrixWorldToObject, matW2O);

            cmb.SetComputeVectorParam(cs, Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(cs, Props.VecWorldSpaceCameraPos, camPos);
            cmb.SetComputeFloatParam(cs, Props.SplatScale, m_SplatScale);
            cmb.SetComputeFloatParam(cs, Props.SplatOpacityScale, m_OpacityScale);
            cmb.SetComputeIntParam(cs, Props.SHOrder, m_SHOrder);
            cmb.SetComputeIntParam(cs, Props.SHOnly, settings.m_SHOnly ? 1 : 0);

            cs.GetKernelThreadGroupSizes((int)KernelIndices.CalcViewData, out uint gsX, out _, out _);
            cmb.DispatchCompute(cs, (int)KernelIndices.CalcViewData, (m_GpuView.count + (int)gsX - 1)/(int)gsX, 1, 1);
        }

        internal void SortPoints(CommandBuffer cmd, Camera cam, Matrix4x4 matrix)
        {
            Matrix4x4 worldToCamMatrix = cam.worldToCameraMatrix;
            worldToCamMatrix.m20 *= -1;
            worldToCamMatrix.m21 *= -1;
            worldToCamMatrix.m22 *= -1;

            // calculate distance to the camera for each splat
            cmd.BeginSample(s_ProfSort);
            GaussianSplatSettings settings = GaussianSplatSettings.instance;
            ComputeShader cs = settings.csUtilities;
            cmd.SetComputeBufferParam(cs, (int)KernelIndices.CalcDistances, Props.SplatSortDistances, m_GpuSortDistances);
            cmd.SetComputeBufferParam(cs, (int)KernelIndices.CalcDistances, Props.SplatSortKeys, m_GpuSortKeys);
            cmd.SetComputeBufferParam(cs, (int)KernelIndices.CalcDistances, Props.SplatChunks, m_GpuChunks);
            cmd.SetComputeBufferParam(cs, (int)KernelIndices.CalcDistances, Props.SplatPos, m_GpuPosData);
            cmd.SetComputeIntParam(cs, Props.SplatFormat, (int)m_Asset.posFormat);
            cmd.SetComputeMatrixParam(cs, Props.MatrixMV, worldToCamMatrix * matrix);
            cmd.SetComputeIntParam(cs, Props.SplatCount, m_SplatCount);
            cmd.SetComputeIntParam(cs, Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);
            cs.GetKernelThreadGroupSizes((int)KernelIndices.CalcDistances, out uint gsX, out _, out _);
            cmd.DispatchCompute(cs, (int)KernelIndices.CalcDistances, (m_GpuSortDistances.count + (int)gsX - 1)/(int)gsX, 1, 1);

            // sort the splats
            EnsureSorterAndRegister();
            m_Sorter.Dispatch(cmd, m_SorterArgs);
            cmd.EndSample(s_ProfSort);
        }

        public void Update()
        {
            var curHash = m_Asset ? m_Asset.dataHash : new Hash128();
            if (m_PrevAsset != m_Asset || m_PrevHash != curHash)
            {
                m_PrevAsset = m_Asset;
                m_PrevHash = curHash;
                if (resourcesAreSetUp)
                {
                    DisposeResourcesForAsset();
                    CreateResourcesForAsset();
                }
                else
                {
                    Debug.LogError($"{nameof(GaussianSplatRenderer)} component is not set up correctly (Resource references are missing), or platform does not support compute shaders");
                }
            }
        }

        public void ActivateCamera(int index)
        {
            Camera mainCam = Camera.main;
            if (!mainCam)
                return;
            if (!m_Asset || m_Asset.cameras == null)
                return;

            var selfTr = transform;
            var camTr = mainCam.transform;
            var prevParent = camTr.parent;
            var cam = m_Asset.cameras[index];
            camTr.parent = selfTr;
            camTr.localPosition = cam.pos;
            camTr.localRotation = Quaternion.LookRotation(cam.axisZ, cam.axisY);
            camTr.parent = prevParent;
            camTr.localScale = Vector3.one;
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(camTr);
#endif
        }

        void ClearGraphicsBuffer(GraphicsBuffer buf)
        {
            ComputeShader cs = GaussianSplatSettings.instance.csUtilities;
            cs.SetBuffer((int)KernelIndices.ClearBuffer, Props.DstBuffer, buf);
            cs.SetInt(Props.BufferSize, buf.count);
            cs.GetKernelThreadGroupSizes((int)KernelIndices.ClearBuffer, out uint gsX, out _, out _);
            cs.Dispatch((int)KernelIndices.ClearBuffer, (int)((buf.count+gsX-1)/gsX), 1, 1);
        }

        void UnionGraphicsBuffers(GraphicsBuffer dst, GraphicsBuffer src)
        {
            ComputeShader cs = GaussianSplatSettings.instance.csUtilities;
            cs.SetBuffer((int)KernelIndices.OrBuffers, Props.SrcBuffer, src);
            cs.SetBuffer((int)KernelIndices.OrBuffers, Props.DstBuffer, dst);
            cs.SetInt(Props.BufferSize, dst.count);
            cs.GetKernelThreadGroupSizes((int)KernelIndices.OrBuffers, out uint gsX, out _, out _);
            cs.Dispatch((int)KernelIndices.OrBuffers, (int)((dst.count+gsX-1)/gsX), 1, 1);
        }

        static float SortableUintToFloat(uint v)
        {
            uint mask = ((v >> 31) - 1) | 0x80000000u;
            return math.asfloat(v ^ mask);
        }

        public void UpdateEditCountsAndBounds()
        {
            if (m_GpuEditSelected == null)
            {
                editSelectedSplats = 0;
                editDeletedSplats = 0;
                editCutSplats = 0;
                editModified = false;
                editSelectedBounds = default;
                return;
            }

            ComputeShader cs = GaussianSplatSettings.instance.csUtilities;
            cs.SetBuffer((int)KernelIndices.InitEditData, Props.DstBuffer, m_GpuEditCountsBounds);
            cs.Dispatch((int)KernelIndices.InitEditData, 1, 1, 1);

            using CommandBuffer cmb = new CommandBuffer();
            SetAssetDataOnCS(cmb, KernelIndices.UpdateEditData);
            cmb.SetComputeBufferParam(cs, (int)KernelIndices.UpdateEditData, Props.DstBuffer, m_GpuEditCountsBounds);
            cmb.SetComputeIntParam(cs, Props.BufferSize, m_GpuEditSelected.count);
            cs.GetKernelThreadGroupSizes((int)KernelIndices.UpdateEditData, out uint gsX, out _, out _);
            cmb.DispatchCompute(cs, (int)KernelIndices.UpdateEditData, (int)((m_GpuEditSelected.count+gsX-1)/gsX), 1, 1);
            Graphics.ExecuteCommandBuffer(cmb);

            uint[] res = new uint[m_GpuEditCountsBounds.count];
            m_GpuEditCountsBounds.GetData(res);
            editSelectedSplats = res[0];
            editDeletedSplats = res[1];
            editCutSplats = res[2];
            Vector3 min = new Vector3(SortableUintToFloat(res[3]), SortableUintToFloat(res[4]), SortableUintToFloat(res[5]));
            Vector3 max = new Vector3(SortableUintToFloat(res[6]), SortableUintToFloat(res[7]), SortableUintToFloat(res[8]));
            Bounds bounds = default;
            bounds.SetMinMax(min, max);
            if (bounds.extents.sqrMagnitude < 0.01)
                bounds.extents = new Vector3(0.1f,0.1f,0.1f);
            editSelectedBounds = bounds;
        }

        void UpdateCutoutsBuffer()
        {
            int bufferSize = m_Cutouts?.Length ?? 0;
            if (bufferSize == 0)
                bufferSize = 1;
            if (m_GpuEditCutouts == null || m_GpuEditCutouts.count != bufferSize)
            {
                m_GpuEditCutouts?.Dispose();
                m_GpuEditCutouts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bufferSize, UnsafeUtility.SizeOf<GaussianCutout.ShaderData>()) { name = "GaussianCutouts" };
            }

            NativeArray<GaussianCutout.ShaderData> data = new(bufferSize, Allocator.Temp);
            if (m_Cutouts != null)
            {
                var matrix = transform.localToWorldMatrix;
                for (var i = 0; i < m_Cutouts.Length; ++i)
                {
                    data[i] = GaussianCutout.GetShaderData(m_Cutouts[i], matrix);
                }
            }

            m_GpuEditCutouts.SetData(data);
            data.Dispose();
        }

        bool EnsureEditingBuffers()
        {
            if (!HasValidAsset || !HasValidRenderSetup)
                return false;

            if (m_GpuEditSelected == null)
            {
                var target = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource |
                             GraphicsBuffer.Target.CopyDestination;
                var size = (m_SplatCount + 31) / 32;
                m_GpuEditSelected = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatSelected"};
                m_GpuEditSelectedMouseDown = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatSelectedInit"};
                m_GpuEditDeleted = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatDeleted"};
                m_GpuEditCountsBounds = new GraphicsBuffer(target, 3 + 6, 4) {name = "GaussianSplatEditData"}; // selected count, deleted bound, cut count, float3 min, float3 max
                ClearGraphicsBuffer(m_GpuEditSelected);
                ClearGraphicsBuffer(m_GpuEditSelectedMouseDown);
                ClearGraphicsBuffer(m_GpuEditDeleted);
            }
            return m_GpuEditSelected != null;
        }

        public void EditStoreSelectionMouseDown()
        {
            if (!EnsureEditingBuffers()) return;
            Graphics.CopyBuffer(m_GpuEditSelected, m_GpuEditSelectedMouseDown);
        }

        public void EditStorePosMouseDown()
        {
            if (m_GpuEditPosMouseDown == null)
            {
                m_GpuEditPosMouseDown = new GraphicsBuffer(m_GpuPosData.target | GraphicsBuffer.Target.CopyDestination, m_GpuPosData.count, m_GpuPosData.stride) {name = "GaussianSplatEditPosMouseDown"};
            }
            Graphics.CopyBuffer(m_GpuPosData, m_GpuEditPosMouseDown);
        }
        public void EditStoreOtherMouseDown()
        {
            if (m_GpuEditOtherMouseDown == null)
            {
                m_GpuEditOtherMouseDown = new GraphicsBuffer(m_GpuOtherData.target | GraphicsBuffer.Target.CopyDestination, m_GpuOtherData.count, m_GpuOtherData.stride) {name = "GaussianSplatEditOtherMouseDown"};
            }
            Graphics.CopyBuffer(m_GpuOtherData, m_GpuEditOtherMouseDown);
        }

        public void EditUpdateSelection(Vector2 rectMin, Vector2 rectMax, Camera cam, bool subtract)
        {
            if (!EnsureEditingBuffers()) return;

            Graphics.CopyBuffer(m_GpuEditSelectedMouseDown, m_GpuEditSelected);

            var tr = transform;
            Matrix4x4 matView = cam.worldToCameraMatrix;
            Matrix4x4 matO2W = tr.localToWorldMatrix;
            Matrix4x4 matW2O = tr.worldToLocalMatrix;
            int screenW = cam.pixelWidth, screenH = cam.pixelHeight;
            Vector4 screenPar = new Vector4(screenW, screenH, 0, 0);
            Vector4 camPos = cam.transform.position;

            using var cmb = new CommandBuffer { name = "SplatSelectionUpdate" };
            SetAssetDataOnCS(cmb, KernelIndices.SelectionUpdate);

            ComputeShader cs = GaussianSplatSettings.instance.csUtilities;
            cmb.SetComputeMatrixParam(cs, Props.MatrixMV, matView * matO2W);
            cmb.SetComputeMatrixParam(cs, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(cs, Props.MatrixWorldToObject, matW2O);

            cmb.SetComputeVectorParam(cs, Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(cs, Props.VecWorldSpaceCameraPos, camPos);

            cmb.SetComputeVectorParam(cs, "_SelectionRect", new Vector4(rectMin.x, rectMax.y, rectMax.x, rectMin.y));
            cmb.SetComputeIntParam(cs, Props.SelectionMode, subtract ? 0 : 1);

            DispatchUtilsAndExecute(cmb, KernelIndices.SelectionUpdate, m_SplatCount);
            UpdateEditCountsAndBounds();
        }

        public void EditTranslateSelection(Vector3 localSpacePosDelta)
        {
            if (!EnsureEditingBuffers()) return;

            using var cmb = new CommandBuffer { name = "SplatTranslateSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.TranslateSelection);

            ComputeShader cs = GaussianSplatSettings.instance.csUtilities;
            cmb.SetComputeVectorParam(cs, Props.SelectionDelta, localSpacePosDelta);

            DispatchUtilsAndExecute(cmb, KernelIndices.TranslateSelection, m_SplatCount);
            UpdateEditCountsAndBounds();
            editModified = true;
        }

        public void EditRotateSelection(Vector3 localSpaceCenter, Matrix4x4 localToWorld, Matrix4x4 worldToLocal, Quaternion rotation)
        {
            if (!EnsureEditingBuffers()) return;
            if (m_GpuEditPosMouseDown == null || m_GpuEditOtherMouseDown == null) return; // should have captured initial state

            using var cmb = new CommandBuffer { name = "SplatRotateSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.RotateSelection);

            ComputeShader cs = GaussianSplatSettings.instance.csUtilities;
            cmb.SetComputeBufferParam(cs, (int)KernelIndices.RotateSelection, Props.SplatPosMouseDown, m_GpuEditPosMouseDown);
            cmb.SetComputeBufferParam(cs, (int)KernelIndices.RotateSelection, Props.SplatOtherMouseDown, m_GpuEditOtherMouseDown);
            cmb.SetComputeVectorParam(cs, Props.SelectionCenter, localSpaceCenter);
            cmb.SetComputeMatrixParam(cs, Props.MatrixObjectToWorld, localToWorld);
            cmb.SetComputeMatrixParam(cs, Props.MatrixWorldToObject, worldToLocal);
            cmb.SetComputeVectorParam(cs, Props.SelectionDeltaRot, new Vector4(rotation.x, rotation.y, rotation.z, rotation.w));

            DispatchUtilsAndExecute(cmb, KernelIndices.RotateSelection, m_SplatCount);
            UpdateEditCountsAndBounds();
            editModified = true;
        }


        public void EditScaleSelection(Vector3 localSpaceCenter, Matrix4x4 localToWorld, Matrix4x4 worldToLocal, Vector3 scale)
        {
            if (!EnsureEditingBuffers()) return;
            if (m_GpuEditPosMouseDown == null) return; // should have captured initial state

            using var cmb = new CommandBuffer { name = "SplatScaleSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.ScaleSelection);

            ComputeShader cs = GaussianSplatSettings.instance.csUtilities;
            cmb.SetComputeBufferParam(cs, (int)KernelIndices.ScaleSelection, Props.SplatPosMouseDown, m_GpuEditPosMouseDown);
            cmb.SetComputeVectorParam(cs, Props.SelectionCenter, localSpaceCenter);
            cmb.SetComputeMatrixParam(cs, Props.MatrixObjectToWorld, localToWorld);
            cmb.SetComputeMatrixParam(cs, Props.MatrixWorldToObject, worldToLocal);
            cmb.SetComputeVectorParam(cs, Props.SelectionDelta, scale);

            DispatchUtilsAndExecute(cmb, KernelIndices.ScaleSelection, m_SplatCount);
            UpdateEditCountsAndBounds();
            editModified = true;
        }

        public void EditDeleteSelected()
        {
            if (!EnsureEditingBuffers()) return;
            UnionGraphicsBuffers(m_GpuEditDeleted, m_GpuEditSelected);
            EditDeselectAll();
            UpdateEditCountsAndBounds();
            if (editDeletedSplats != 0)
                editModified = true;
        }

        public void EditSelectAll()
        {
            if (!EnsureEditingBuffers()) return;
            using var cmb = new CommandBuffer { name = "SplatSelectAll" };
            SetAssetDataOnCS(cmb, KernelIndices.SelectAll);
            ComputeShader cs = GaussianSplatSettings.instance.csUtilities;
            cmb.SetComputeBufferParam(cs, (int)KernelIndices.SelectAll, Props.DstBuffer, m_GpuEditSelected);
            cmb.SetComputeIntParam(cs, Props.BufferSize, m_GpuEditSelected.count);
            DispatchUtilsAndExecute(cmb, KernelIndices.SelectAll, m_GpuEditSelected.count);
            UpdateEditCountsAndBounds();
        }

        public void EditDeselectAll()
        {
            if (!EnsureEditingBuffers()) return;
            ClearGraphicsBuffer(m_GpuEditSelected);
            UpdateEditCountsAndBounds();
        }

        public void EditInvertSelection()
        {
            if (!EnsureEditingBuffers()) return;

            using var cmb = new CommandBuffer { name = "SplatInvertSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.InvertSelection);
            ComputeShader cs = GaussianSplatSettings.instance.csUtilities;
            cmb.SetComputeBufferParam(cs, (int)KernelIndices.InvertSelection, Props.DstBuffer, m_GpuEditSelected);
            cmb.SetComputeIntParam(cs, Props.BufferSize, m_GpuEditSelected.count);
            DispatchUtilsAndExecute(cmb, KernelIndices.InvertSelection, m_GpuEditSelected.count);
            UpdateEditCountsAndBounds();
        }

        public bool EditExportData(GraphicsBuffer dstData, bool bakeTransform)
        {
            if (!EnsureEditingBuffers()) return false;

            int flags = 0;
            var tr = transform;
            Quaternion bakeRot = tr.localRotation;
            Vector3 bakeScale = tr.localScale;

            if (bakeTransform)
                flags = 1;

            using var cmb = new CommandBuffer { name = "SplatExportData" };
            SetAssetDataOnCS(cmb, KernelIndices.ExportData);
            ComputeShader cs = GaussianSplatSettings.instance.csUtilities;
            cmb.SetComputeIntParam(cs, "_ExportTransformFlags", flags);
            cmb.SetComputeVectorParam(cs, "_ExportTransformRotation", new Vector4(bakeRot.x, bakeRot.y, bakeRot.z, bakeRot.w));
            cmb.SetComputeVectorParam(cs, "_ExportTransformScale", bakeScale);
            cmb.SetComputeMatrixParam(cs, Props.MatrixObjectToWorld, tr.localToWorldMatrix);
            cmb.SetComputeBufferParam(cs, (int)KernelIndices.ExportData, "_ExportBuffer", dstData);

            DispatchUtilsAndExecute(cmb, KernelIndices.ExportData, m_SplatCount);
            return true;
        }

        public void EditSetSplatCount(int newSplatCount)
        {
            if (newSplatCount <= 0 || newSplatCount > GaussianSplatAsset.kMaxSplats)
            {
                Debug.LogError($"Invalid new splat count: {newSplatCount}");
                return;
            }
            if (asset.chunkData != null)
            {
                Debug.LogError("Only splats with VeryHigh quality can be resized");
                return;
            }
            if (newSplatCount == splatCount)
                return;

            int posStride = (int)(asset.posData.dataSize / asset.splatCount);
            int otherStride = (int)(asset.otherData.dataSize / asset.splatCount);
            int shStride = (int) (asset.shData.dataSize / asset.splatCount);

            // create new GPU buffers
            var newPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, newSplatCount * posStride / 4, 4) { name = "GaussianPosData" };
            var newOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, newSplatCount * otherStride / 4, 4) { name = "GaussianOtherData" };
            var newSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, newSplatCount * shStride / 4, 4) { name = "GaussianSHData" };

            // new texture is a RenderTexture so we can write to it from a compute shader
            var (texWidth, texHeight) = GaussianSplatAsset.CalcTextureSize(newSplatCount);
            var texFormat = GaussianSplatAsset.ColorFormatToGraphics(asset.colorFormat);
            var newColorData = new RenderTexture(texWidth, texHeight, texFormat, GraphicsFormat.None) { name = "GaussianColorData", enableRandomWrite = true };
            newColorData.Create();

            // selected/deleted buffers
            var selTarget = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource | GraphicsBuffer.Target.CopyDestination;
            var selSize = (newSplatCount + 31) / 32;
            var newEditSelected = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatSelected"};
            var newEditSelectedMouseDown = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatSelectedInit"};
            var newEditDeleted = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatDeleted"};
            ClearGraphicsBuffer(newEditSelected);
            ClearGraphicsBuffer(newEditSelectedMouseDown);
            ClearGraphicsBuffer(newEditDeleted);

            var newGpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, newSplatCount, kGpuViewDataSize);
            InitSortBuffers(newSplatCount);

            // copy existing data over into new buffers
            EditCopySplats(transform, newPosData, newOtherData, newSHData, newColorData, newEditDeleted, newSplatCount, 0, 0, m_SplatCount);

            // use the new buffers and the new splat count
            m_GpuPosData.Dispose();
            m_GpuOtherData.Dispose();
            m_GpuSHData.Dispose();
            DestroyImmediate(m_GpuColorData);
            m_GpuView.Dispose();

            m_GpuEditSelected?.Dispose();
            m_GpuEditSelectedMouseDown?.Dispose();
            m_GpuEditDeleted?.Dispose();

            m_GpuPosData = newPosData;
            m_GpuOtherData = newOtherData;
            m_GpuSHData = newSHData;
            m_GpuColorData = newColorData;
            m_GpuView = newGpuView;
            m_GpuEditSelected = newEditSelected;
            m_GpuEditSelectedMouseDown = newEditSelectedMouseDown;
            m_GpuEditDeleted = newEditDeleted;

            DisposeBuffer(ref m_GpuEditPosMouseDown);
            DisposeBuffer(ref m_GpuEditOtherMouseDown);

            m_SplatCount = newSplatCount;
            editModified = true;
        }

        public void EditCopySplatsInto(GaussianSplatRenderer dst, int copySrcStartIndex, int copyDstStartIndex, int copyCount)
        {
            EditCopySplats(
                dst.transform,
                dst.m_GpuPosData, dst.m_GpuOtherData, dst.m_GpuSHData, dst.m_GpuColorData, dst.m_GpuEditDeleted,
                dst.splatCount,
                copySrcStartIndex, copyDstStartIndex, copyCount);
            dst.editModified = true;
        }

        public void EditCopySplats(
            Transform dstTransform,
            GraphicsBuffer dstPos, GraphicsBuffer dstOther, GraphicsBuffer dstSH, Texture dstColor,
            GraphicsBuffer dstEditDeleted,
            int dstSize,
            int copySrcStartIndex, int copyDstStartIndex, int copyCount)
        {
            if (!EnsureEditingBuffers()) return;

            Matrix4x4 copyMatrix = dstTransform.worldToLocalMatrix * transform.localToWorldMatrix;
            Quaternion copyRot = copyMatrix.rotation;
            Vector3 copyScale = copyMatrix.lossyScale;

            using var cmb = new CommandBuffer { name = "SplatCopy" };
            SetAssetDataOnCS(cmb, KernelIndices.CopySplats);

            ComputeShader cs = GaussianSplatSettings.instance.csUtilities;
            cmb.SetComputeBufferParam(cs, (int)KernelIndices.CopySplats, "_CopyDstPos", dstPos);
            cmb.SetComputeBufferParam(cs, (int)KernelIndices.CopySplats, "_CopyDstOther", dstOther);
            cmb.SetComputeBufferParam(cs, (int)KernelIndices.CopySplats, "_CopyDstSH", dstSH);
            cmb.SetComputeTextureParam(cs, (int)KernelIndices.CopySplats, "_CopyDstColor", dstColor);
            cmb.SetComputeBufferParam(cs, (int)KernelIndices.CopySplats, "_CopyDstEditDeleted", dstEditDeleted);

            cmb.SetComputeIntParam(cs, "_CopyDstSize", dstSize);
            cmb.SetComputeIntParam(cs, "_CopySrcStartIndex", copySrcStartIndex);
            cmb.SetComputeIntParam(cs, "_CopyDstStartIndex", copyDstStartIndex);
            cmb.SetComputeIntParam(cs, "_CopyCount", copyCount);

            cmb.SetComputeVectorParam(cs, "_CopyTransformRotation", new Vector4(copyRot.x, copyRot.y, copyRot.z, copyRot.w));
            cmb.SetComputeVectorParam(cs, "_CopyTransformScale", copyScale);
            cmb.SetComputeMatrixParam(cs, "_CopyTransformMatrix", copyMatrix);

            DispatchUtilsAndExecute(cmb, KernelIndices.CopySplats, copyCount);
        }

        void DispatchUtilsAndExecute(CommandBuffer cmb, KernelIndices kernel, int count)
        {
            ComputeShader cs = GaussianSplatSettings.instance.csUtilities;
            cs.GetKernelThreadGroupSizes((int)kernel, out uint gsX, out _, out _);
            cmb.DispatchCompute(cs, (int)kernel, (int)((count + gsX - 1)/gsX), 1, 1);
            Graphics.ExecuteCommandBuffer(cmb);
        }

        public GraphicsBuffer GpuEditDeleted => m_GpuEditDeleted;
    }
}