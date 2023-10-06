using System;
using System.Collections.Generic;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

class GaussianSplatRenderSystem
{
    static ProfilerMarker s_ProfDraw = new(ProfilerCategory.Render, "GaussianSplat.Draw", MarkerFlags.SampleGPU);
    internal static ProfilerMarker s_ProfCompose = new(ProfilerCategory.Render, "GaussianSplat.Compose", MarkerFlags.SampleGPU);
    
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

        // sort them by depth from camera
        var camTr = cam.transform;
        m_ActiveSplats.Sort((a, b) =>
        {
            var trA = a.Item1.transform;
            var trB = b.Item1.transform;
            var posA = camTr.InverseTransformPoint(trA.position);
            var posB = camTr.InverseTransformPoint(trB.position);
            return posA.z.CompareTo(posB.z);
        });

        return true;
    }

    public Material SortAndRenderSplats(Camera cam, CommandBuffer cmb)
    {
        Material matComposite = null;
        foreach (var kvp in m_ActiveSplats)
        {
            var gs = kvp.Item1;
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
                GaussianSplatRenderer.RenderMode.DebugChunkBounds => gs.m_MatDebugBoxes,
                _ => gs.m_MatSplats
            };
            if (displayMat == null)
                continue;

            gs.SetAssetDataOnMaterial(mpb);
            mpb.SetBuffer("_SplatChunks", gs.m_GpuChunks);
            mpb.SetInteger("_SplatChunkCount", gs.m_GpuChunks.count);

            mpb.SetBuffer("_SplatViewData", gs.m_GpuView);

            mpb.SetBuffer("_OrderBuffer", gs.m_GpuSortKeys);
            mpb.SetFloat("_SplatScale", gs.m_SplatScale);
            mpb.SetFloat("_SplatOpacityScale", gs.m_OpacityScale);
            mpb.SetFloat("_SplatSize", gs.m_PointDisplaySize);
            mpb.SetInteger("_SplatCount", gs.asset.m_SplatCount);
            mpb.SetInteger("_SHOrder", gs.m_SHOrder);
            mpb.SetInteger("_DisplayIndex", gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugPointIndices ? 1 : 0);
            mpb.SetInteger("_DisplayChunks", gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugChunkBounds ? 1 : 0);

            gs.CalcViewData(cmb, cam, matrix);

            // draw
            int indexCount = 6;
            int instanceCount = gs.asset.m_SplatCount;
            MeshTopology topology = MeshTopology.Triangles;
            if (gs.m_RenderMode is GaussianSplatRenderer.RenderMode.DebugBoxes or GaussianSplatRenderer.RenderMode.DebugChunkBounds)
                indexCount = 36;
            if (gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugChunkBounds)
                instanceCount = gs.m_GpuChunks.count;

            cmb.BeginSample(s_ProfDraw);
            cmb.DrawProcedural(gs.m_GpuIndexBuffer, matrix, displayMat, 0, topology, indexCount, instanceCount, mpb);
            cmb.EndSample(s_ProfDraw);
        }
        return matComposite;
    }

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

        InitialClearCmdBuffer(cam);

        int rtNameID = Shader.PropertyToID("_GaussianSplatRT");
        m_CommandBuffer.GetTemporaryRT(rtNameID, -1, -1, 0, FilterMode.Point, GraphicsFormat.R16G16B16A16_SFloat);
        m_CommandBuffer.SetRenderTarget(rtNameID, BuiltinRenderTextureType.CurrentActive);
        m_CommandBuffer.ClearRenderTarget(RTClearFlags.Color, new Color(0, 0, 0, 0), 0, 0);

        // add sorting, view calc and drawing commands for each splat object
        Material matComposite = SortAndRenderSplats(cam, m_CommandBuffer);

        // compose
        m_CommandBuffer.BeginSample(s_ProfCompose);
        m_CommandBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
        m_CommandBuffer.DrawProcedural(Matrix4x4.identity, matComposite, 0, MeshTopology.Triangles, 3, 1);
        m_CommandBuffer.EndSample(s_ProfCompose);
        m_CommandBuffer.ReleaseTemporaryRT(rtNameID);
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
        DebugChunkBounds,
    }

    [Header("Data Asset")]

    public GaussianSplatAsset m_Asset;

    [Header("Render Options")]

    [Range(0.1f, 2.0f)] [Tooltip("Additional scaling factor for the splats")]
    public float m_SplatScale = 1.0f;
    [Range(0.05f, 20.0f)]
    [Tooltip("Additional scaling factor for opacity")]
    public float m_OpacityScale = 1.0f;
    [Range(0, 3)] [Tooltip("Spherical Harmonics order to use")]
    public int m_SHOrder = 3;
    [Range(1,30)] [Tooltip("Sort splats only every N frames")]
    public int m_SortNthFrame = 1;

    [Header("Debugging Tweaks")]

    public RenderMode m_RenderMode = RenderMode.Splats;
    [Range(1.0f,15.0f)] public float m_PointDisplaySize = 3.0f;

    [Header("Resources")]

    public Shader m_ShaderSplats;
    public Shader m_ShaderComposite;
    public Shader m_ShaderDebugPoints;
    public Shader m_ShaderDebugBoxes;
    [Tooltip("Gaussian splatting utilities compute shader")]
    public ComputeShader m_CSSplatUtilities;
    [Tooltip("AMD FidelityFX sort compute shader")]
    public ComputeShader m_CSFfxSort;

    GraphicsBuffer m_GpuSortDistances;
    internal GraphicsBuffer m_GpuSortKeys;
    GraphicsBuffer m_GpuPosData;
    GraphicsBuffer m_GpuOtherData;
    GraphicsBuffer m_GpuSHData;
    Texture2D m_GpuColorData;
    internal GraphicsBuffer m_GpuChunks;
    internal GraphicsBuffer m_GpuView;
    internal GraphicsBuffer m_GpuIndexBuffer;
    GraphicsBuffer m_GpuSplatSelectedInitBuffer;
    GraphicsBuffer m_GpuSplatSelectedBuffer;
    GraphicsBuffer m_GpuSplatDeletedBuffer;
    GraphicsBuffer m_GpuSplatCounterBuffer;

    FfxParallelSort m_SorterFfx;
    FfxParallelSort.Args m_SorterFfxArgs;

    internal Material m_MatSplats;
    internal Material m_MatComposite;
    internal Material m_MatDebugPoints;
    internal Material m_MatDebugBoxes;

    internal int m_FrameCounter;
    GaussianSplatAsset m_PrevAsset;
    Hash128 m_PrevHash;

    static readonly ProfilerMarker s_ProfSort = new(ProfilerCategory.Render, "GaussianSplat.Sort", MarkerFlags.SampleGPU);
    static readonly ProfilerMarker s_ProfView = new(ProfilerCategory.Render, "GaussianSplat.View", MarkerFlags.SampleGPU);

    [NonSerialized] bool m_EditModified;
    [NonSerialized] uint m_EditSelectedSplats;
    [NonSerialized] uint m_EditDeletedSplats;

    public bool editModified => m_EditModified;
    public uint editSelectedSplats => m_EditSelectedSplats;
    public uint editDeletedSplats => m_EditDeletedSplats;

    public GaussianSplatAsset asset => m_Asset;
    
    const int kKernelSetIndices = 0;
    const int kKernelCalcDistances = 1;
    const int kKernelCalcViewData = 2;
    const int kKernelCountBuffer = 3;
    const int kKernelClearBuffer = 4;
    const int kKernelInvertBuffer = 5;
    const int kKernelOrBuffers = 6;
    const int kKernelSelectionUpdate = 7;
    const int kKernelExportData = 8;

    public bool HasValidAsset =>
        m_Asset != null &&
        m_Asset.m_SplatCount > 0 &&
        m_Asset.m_FormatVersion == GaussianSplatAsset.kCurrentVersion &&
        m_Asset.m_PosData != null &&
        m_Asset.m_OtherData != null &&
        m_Asset.m_SHData != null &&
        m_Asset.m_ChunkData != null && 
        m_Asset.m_ColorData != null;
    public bool HasValidRenderSetup => m_GpuPosData != null && m_GpuOtherData != null && m_GpuChunks != null;

    void CreateResourcesForAsset()
    {
        if (!HasValidAsset)
            return;

        m_GpuPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, (int) (asset.m_PosData.dataSize / 4), 4) { name = "GaussianPosData" };
        m_GpuPosData.SetData(asset.m_PosData.GetData<uint>());
        m_GpuOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, (int) (asset.m_OtherData.dataSize / 4), 4) { name = "GaussianOtherData" };
        m_GpuOtherData.SetData(asset.m_OtherData.GetData<uint>());
        m_GpuSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, (int) (asset.m_SHData.dataSize / 4), 4) { name = "GaussianSHData" };
        m_GpuSHData.SetData(asset.m_SHData.GetData<uint>());
        m_GpuColorData = new Texture2D(asset.m_ColorWidth, asset.m_ColorHeight, asset.m_ColorFormat, TextureCreationFlags.DontInitializePixels | TextureCreationFlags.IgnoreMipmapLimit | TextureCreationFlags.DontUploadUponCreate) { name = "GaussianColorData" };
        m_GpuColorData.SetPixelData(asset.m_ColorData.GetData<byte>(), 0);
        m_GpuColorData.Apply(false, true);
        m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, (int)(asset.m_ChunkData.dataSize / UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()) , UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()) { name = "GaussianChunkData" };
        m_GpuChunks.SetData(asset.m_ChunkData.GetData<GaussianSplatAsset.ChunkInfo>());

        m_GpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_Asset.m_SplatCount, 40);
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

        m_GpuSortDistances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_Asset.m_SplatCount, 4) { name = "GaussianSplatSortDistances" };
        m_GpuSortKeys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_Asset.m_SplatCount, 4) { name = "GaussianSplatSortIndices" };

        // init keys buffer to splat indices
        m_CSSplatUtilities.SetBuffer(kKernelSetIndices, "_SplatSortKeys", m_GpuSortKeys);
        m_CSSplatUtilities.SetInt("_SplatCount", m_GpuSortDistances.count);
        m_CSSplatUtilities.GetKernelThreadGroupSizes(kKernelSetIndices, out uint gsX, out _, out _);
        m_CSSplatUtilities.Dispatch(kKernelSetIndices, (m_GpuSortDistances.count + (int)gsX - 1)/(int)gsX, 1, 1);

        m_SorterFfxArgs.inputKeys = m_GpuSortDistances;
        m_SorterFfxArgs.inputValues = m_GpuSortKeys;
        m_SorterFfxArgs.count = (uint) m_Asset.m_SplatCount;
        if (m_SorterFfx.Valid)
            m_SorterFfxArgs.resources = FfxParallelSort.SupportResources.Load((uint)m_Asset.m_SplatCount);
    }

    public void OnEnable()
    {
        m_FrameCounter = 0;
        if (m_ShaderSplats == null || m_ShaderComposite == null || m_ShaderDebugPoints == null || m_ShaderDebugBoxes == null || m_CSSplatUtilities == null)
            return;
        if (!SystemInfo.supportsComputeShaders)
            return;

        m_MatSplats = new Material(m_ShaderSplats) {name = "GaussianSplats"};
        m_MatComposite = new Material(m_ShaderComposite) {name = "GaussianClearDstAlpha"};
        m_MatDebugPoints = new Material(m_ShaderDebugPoints) {name = "GaussianDebugPoints"};
        m_MatDebugBoxes = new Material(m_ShaderDebugBoxes) {name = "GaussianDebugBoxes"};

        m_SorterFfx = new FfxParallelSort(m_CSFfxSort);
        GaussianSplatRenderSystem.instance.RegisterSplat(this);
        
        CreateResourcesForAsset();
    }

    void SetAssetDataOnCS(CommandBuffer cmb, ComputeShader cs, int kernelIndex)
    {
        cmb.SetComputeBufferParam(cs, kernelIndex, "_SplatPos", m_GpuPosData);
        cmb.SetComputeBufferParam(cs, kernelIndex, "_SplatOther", m_GpuOtherData);
        cmb.SetComputeBufferParam(cs, kernelIndex, "_SplatSH", m_GpuSHData);
        cmb.SetComputeTextureParam(cs, kernelIndex, "_SplatColor", m_GpuColorData);
        cmb.SetComputeBufferParam(cs, kernelIndex, "_SplatSelectedBits", m_GpuSplatSelectedBuffer ?? m_GpuPosData);
        cmb.SetComputeBufferParam(cs, kernelIndex, "_SplatDeletedBits", m_GpuSplatDeletedBuffer ?? m_GpuPosData);
        cmb.SetComputeIntParam(cs, "_SplatBitsValid", m_GpuSplatSelectedBuffer != null && m_GpuSplatDeletedBuffer != null ? 1 : 0);
        uint format = (uint)m_Asset.m_PosFormat | ((uint)m_Asset.m_ScaleFormat << 8) | ((uint)m_Asset.m_SHFormat << 16);
        cmb.SetComputeIntParam(cs, "_SplatFormat", (int)format);
    }

    internal void SetAssetDataOnMaterial(MaterialPropertyBlock mat)
    {
        mat.SetBuffer("_SplatPos", m_GpuPosData);
        mat.SetBuffer("_SplatOther", m_GpuOtherData);
        mat.SetBuffer("_SplatSH", m_GpuSHData);
        mat.SetTexture("_SplatColor", m_GpuColorData);
        mat.SetBuffer("_SplatSelectedBits", m_GpuSplatSelectedBuffer ?? m_GpuPosData);
        mat.SetBuffer("_SplatDeletedBits", m_GpuSplatDeletedBuffer ?? m_GpuPosData);
        mat.SetInt("_SplatBitsValid", m_GpuSplatSelectedBuffer != null && m_GpuSplatDeletedBuffer != null ? 1 : 0);
        uint format = (uint)m_Asset.m_PosFormat | ((uint)m_Asset.m_ScaleFormat << 8) | ((uint)m_Asset.m_SHFormat << 16);
        mat.SetInteger("_SplatFormat", (int)format);
    }

    void DisposeResourcesForAsset()
    {
        m_GpuPosData?.Dispose();
        m_GpuOtherData?.Dispose();
        m_GpuSHData?.Dispose();
        DestroyImmediate(m_GpuColorData);
        m_GpuChunks?.Dispose();
        m_GpuView?.Dispose();
        m_GpuIndexBuffer?.Dispose();
        m_GpuSortDistances?.Dispose();
        m_GpuSortKeys?.Dispose();
        m_GpuSplatSelectedInitBuffer?.Dispose();
        m_GpuSplatSelectedBuffer?.Dispose();
        m_GpuSplatDeletedBuffer?.Dispose();
        m_GpuSplatCounterBuffer?.Dispose();
        m_SorterFfxArgs.resources.Dispose();

        m_GpuPosData = null;
        m_GpuOtherData = null;
        m_GpuSHData = null;
        m_GpuColorData = null;
        m_GpuChunks = null;
        m_GpuView = null;
        m_GpuIndexBuffer = null;
        m_GpuSortDistances = null;
        m_GpuSortKeys = null;
        m_GpuSplatSelectedInitBuffer = null;
        m_GpuSplatSelectedBuffer = null;
        m_GpuSplatDeletedBuffer = null;
        m_GpuSplatCounterBuffer = null;
        
        m_EditSelectedSplats = 0;
        m_EditDeletedSplats = 0;
        m_EditModified = false;
    }

    public void OnDisable()
    {
        DisposeResourcesForAsset();
        GaussianSplatRenderSystem.instance.UnregisterSplat(this);

        DestroyImmediate(m_MatSplats);
        DestroyImmediate(m_MatComposite);
        DestroyImmediate(m_MatDebugPoints);
        DestroyImmediate(m_MatDebugBoxes);
    }

    internal void CalcViewData(CommandBuffer cmb, Camera cam, Matrix4x4 matrix)
    {
        if (cam.cameraType == CameraType.Preview)
            return;

        using var prof = s_ProfView.Auto();

        var tr = transform;

        Matrix4x4 matView = cam.worldToCameraMatrix;
        Matrix4x4 matProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
        Matrix4x4 matO2W = tr.localToWorldMatrix;
        Matrix4x4 matW2O = tr.worldToLocalMatrix;
        int screenW = cam.pixelWidth, screenH = cam.pixelHeight;
        Vector4 screenPar = new Vector4(screenW, screenH, 0, 0);
        Vector4 camPos = cam.transform.position;

        // calculate view dependent data for each splat
        SetAssetDataOnCS(cmb, m_CSSplatUtilities, kKernelCalcViewData);

        cmb.SetComputeIntParam(m_CSSplatUtilities, "_SplatCount", m_GpuView.count);
        cmb.SetComputeBufferParam(m_CSSplatUtilities, kKernelCalcViewData, "_SplatViewData", m_GpuView);
        cmb.SetComputeBufferParam(m_CSSplatUtilities, kKernelCalcViewData, "_OrderBuffer", m_GpuSortKeys);
        cmb.SetComputeBufferParam(m_CSSplatUtilities, kKernelCalcViewData, "_SplatChunks", m_GpuChunks);

        cmb.SetComputeMatrixParam(m_CSSplatUtilities, "_MatrixVP", matProj * matView);
        cmb.SetComputeMatrixParam(m_CSSplatUtilities, "_MatrixMV", matView * matO2W);
        cmb.SetComputeMatrixParam(m_CSSplatUtilities, "_MatrixP", matProj);
        cmb.SetComputeMatrixParam(m_CSSplatUtilities, "_MatrixObjectToWorld", matO2W);
        cmb.SetComputeMatrixParam(m_CSSplatUtilities, "_MatrixWorldToObject", matW2O);

        cmb.SetComputeVectorParam(m_CSSplatUtilities, "_VecScreenParams", screenPar);
        cmb.SetComputeVectorParam(m_CSSplatUtilities, "_VecWorldSpaceCameraPos", camPos);
        cmb.SetComputeFloatParam(m_CSSplatUtilities, "_SplatScale", m_SplatScale);
        cmb.SetComputeFloatParam(m_CSSplatUtilities, "_SplatOpacityScale", m_OpacityScale);
        cmb.SetComputeIntParam(m_CSSplatUtilities, "_SHOrder", m_SHOrder);

        m_CSSplatUtilities.GetKernelThreadGroupSizes(kKernelCalcViewData, out uint gsX, out uint gsY, out uint gsZ);
        cmb.DispatchCompute(m_CSSplatUtilities, kKernelCalcViewData, (m_GpuView.count + (int)gsX - 1)/(int)gsX, 1, 1);
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
        cmd.SetComputeBufferParam(m_CSSplatUtilities, kKernelCalcDistances, "_SplatSortDistances", m_GpuSortDistances);
        cmd.SetComputeBufferParam(m_CSSplatUtilities, kKernelCalcDistances, "_SplatSortKeys", m_GpuSortKeys);
        cmd.SetComputeBufferParam(m_CSSplatUtilities, kKernelCalcDistances, "_SplatChunks", m_GpuChunks);
        cmd.SetComputeBufferParam(m_CSSplatUtilities, kKernelCalcDistances, "_SplatPos", m_GpuPosData);
        cmd.SetComputeIntParam(m_CSSplatUtilities, "_SplatFormat", (int)m_Asset.m_PosFormat);
        cmd.SetComputeMatrixParam(m_CSSplatUtilities, "_LocalToWorldMatrix", matrix);
        cmd.SetComputeMatrixParam(m_CSSplatUtilities, "_WorldToCameraMatrix", worldToCamMatrix);
        cmd.SetComputeIntParam(m_CSSplatUtilities, "_SplatCount", m_Asset.m_SplatCount);
        m_CSSplatUtilities.GetKernelThreadGroupSizes(kKernelCalcDistances, out uint gsX, out _, out _);
        cmd.DispatchCompute(m_CSSplatUtilities, kKernelCalcDistances, (m_GpuSortDistances.count + (int)gsX - 1)/(int)gsX, 1, 1);

        // sort the splats
        m_SorterFfx.Dispatch(cmd, m_SorterFfxArgs);
        cmd.EndSample(s_ProfSort);
    }

    public void Update()
    {
        var curHash = m_Asset ? m_Asset.m_DataHash : new Hash128();
        if (m_PrevAsset != m_Asset || m_PrevHash != curHash)
        {
            m_PrevAsset = m_Asset;
            m_PrevHash = curHash;
            DisposeResourcesForAsset();
            CreateResourcesForAsset();
        }
    }
    
    public void ActivateCamera(int index)
    {
        Camera mainCam = Camera.main;
        if (!mainCam)
            return;
        if (!m_Asset || m_Asset.m_Cameras == null)
            return;

        var selfTr = transform;
        var camTr = mainCam.transform;
        var prevParent = camTr.parent; 
        var cam = m_Asset.m_Cameras[index];
        camTr.parent = selfTr;
        camTr.localPosition = cam.pos;
        camTr.localRotation = Quaternion.LookRotation(cam.axisZ, cam.axisY);
        camTr.parent = prevParent;
        camTr.localScale = Vector3.one;
#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(camTr);
#endif
    }
    
    void ClearGraphicsBuffer(GraphicsBuffer buf, uint value = 0)
    {
        m_CSSplatUtilities.SetBuffer(kKernelClearBuffer, "_DstBuffer", buf);
        m_CSSplatUtilities.SetInt("_BufferSize", buf.count);
        m_CSSplatUtilities.SetInt("_DstBufferValue", (int)value);
        m_CSSplatUtilities.GetKernelThreadGroupSizes(kKernelClearBuffer, out uint gsX, out _, out _);
        m_CSSplatUtilities.Dispatch(kKernelClearBuffer, (int)((buf.count+gsX-1)/gsX), 1, 1);
    }
    
    void InvertGraphicsBuffer(GraphicsBuffer buf)
    {
        m_CSSplatUtilities.SetBuffer(kKernelInvertBuffer, "_DstBuffer", buf);
        m_CSSplatUtilities.SetInt("_BufferSize", buf.count);
        m_CSSplatUtilities.GetKernelThreadGroupSizes(kKernelInvertBuffer, out uint gsX, out _, out _);
        m_CSSplatUtilities.Dispatch(kKernelInvertBuffer, (int)((buf.count+gsX-1)/gsX), 1, 1);
    }
    
    void UnionGraphicsBuffers(GraphicsBuffer dst, GraphicsBuffer src)
    {
        m_CSSplatUtilities.SetBuffer(kKernelOrBuffers, "_SrcBuffer", src);
        m_CSSplatUtilities.SetBuffer(kKernelOrBuffers, "_DstBuffer", dst);
        m_CSSplatUtilities.SetInt("_BufferSize", dst.count);
        m_CSSplatUtilities.GetKernelThreadGroupSizes(kKernelOrBuffers, out uint gsX, out _, out _);
        m_CSSplatUtilities.Dispatch(kKernelOrBuffers, (int)((dst.count+gsX-1)/gsX), 1, 1);
    }

    void UpdateEditCounts()
    {
        if (m_GpuSplatSelectedBuffer == null)
        {
            m_EditSelectedSplats = 0;
            m_EditDeletedSplats = 0;
            m_EditModified = false;
            return;
        }
        
        ClearGraphicsBuffer(m_GpuSplatCounterBuffer);
        m_CSSplatUtilities.SetBuffer(kKernelCountBuffer, "_SplatSelectedBits", m_GpuSplatSelectedBuffer);
        m_CSSplatUtilities.SetBuffer(kKernelCountBuffer, "_SplatDeletedBits", m_GpuSplatDeletedBuffer);
        m_CSSplatUtilities.SetBuffer(kKernelCountBuffer, "_DstBuffer", m_GpuSplatCounterBuffer);
        m_CSSplatUtilities.SetInt("_BufferSize", m_GpuSplatSelectedBuffer.count);
        m_CSSplatUtilities.GetKernelThreadGroupSizes(kKernelCountBuffer, out uint gsX, out _, out _);
        m_CSSplatUtilities.Dispatch(kKernelCountBuffer, (int)((m_GpuSplatSelectedBuffer.count+gsX-1)/gsX), 1, 1);
        uint[] res = {0u, 0u};
        m_GpuSplatCounterBuffer.GetData(res);
        m_EditSelectedSplats = res[0];
        m_EditDeletedSplats = res[1];
    }

    bool EnsureSelectionBuffers()
    {
        if (!HasValidAsset || !HasValidRenderSetup)
            return false;
        
        if (m_GpuSplatSelectedBuffer == null)
        {
            var target = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource |
                         GraphicsBuffer.Target.CopyDestination;
            var size = (m_Asset.m_SplatCount + 31) / 32;
            m_GpuSplatSelectedBuffer = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatSelected"};
            m_GpuSplatSelectedInitBuffer = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatSelectedInit"};
            m_GpuSplatDeletedBuffer = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatDeleted"};
            m_GpuSplatCounterBuffer = new GraphicsBuffer(target, 2, 4) {name = "GaussianSplatCounter"};
            ClearGraphicsBuffer(m_GpuSplatSelectedBuffer);
            ClearGraphicsBuffer(m_GpuSplatSelectedInitBuffer);
            ClearGraphicsBuffer(m_GpuSplatDeletedBuffer);
        }
        return m_GpuSplatSelectedBuffer != null;
    }

    public void EditStoreInitialSelection()
    {
        if (!EnsureSelectionBuffers()) return;
        Graphics.CopyBuffer(m_GpuSplatSelectedBuffer, m_GpuSplatSelectedInitBuffer);
    }

    public void EditUpdateSelection(Vector2 rectMin, Vector2 rectMax, Camera cam)
    {
        if (!EnsureSelectionBuffers()) return;

        Graphics.CopyBuffer(m_GpuSplatSelectedInitBuffer, m_GpuSplatSelectedBuffer);
        
        var tr = transform;
        Matrix4x4 matView = cam.worldToCameraMatrix;
        Matrix4x4 matProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
        Matrix4x4 matO2W = tr.localToWorldMatrix;
        Matrix4x4 matW2O = tr.worldToLocalMatrix;
        int screenW = cam.pixelWidth, screenH = cam.pixelHeight;
        Vector4 screenPar = new Vector4(screenW, screenH, 0, 0);
        Vector4 camPos = cam.transform.position;

        var cmb = new CommandBuffer { name = "SplatSelectionUpdate" };
        SetAssetDataOnCS(cmb, m_CSSplatUtilities, kKernelSelectionUpdate);
        cmb.SetComputeIntParam(m_CSSplatUtilities, "_SplatCount", m_Asset.m_SplatCount);
        cmb.SetComputeBufferParam(m_CSSplatUtilities, kKernelSelectionUpdate, "_SplatChunks", m_GpuChunks);

        cmb.SetComputeMatrixParam(m_CSSplatUtilities, "_MatrixVP", matProj * matView);
        cmb.SetComputeMatrixParam(m_CSSplatUtilities, "_MatrixMV", matView * matO2W);
        cmb.SetComputeMatrixParam(m_CSSplatUtilities, "_MatrixP", matProj);
        cmb.SetComputeMatrixParam(m_CSSplatUtilities, "_MatrixObjectToWorld", matO2W);
        cmb.SetComputeMatrixParam(m_CSSplatUtilities, "_MatrixWorldToObject", matW2O);

        cmb.SetComputeVectorParam(m_CSSplatUtilities, "_VecScreenParams", screenPar);
        cmb.SetComputeVectorParam(m_CSSplatUtilities, "_VecWorldSpaceCameraPos", camPos);
        
        cmb.SetComputeVectorParam(m_CSSplatUtilities, "_SelectionRect", new Vector4(rectMin.x, rectMax.y, rectMax.x, rectMin.y));

        m_CSSplatUtilities.GetKernelThreadGroupSizes(kKernelSelectionUpdate, out uint gsX, out _, out _);
        cmb.DispatchCompute(m_CSSplatUtilities, kKernelSelectionUpdate, (m_Asset.m_SplatCount + (int)gsX - 1)/(int)gsX, 1, 1);
        Graphics.ExecuteCommandBuffer(cmb);
        cmb.Dispose();
        UpdateEditCounts();
    }

    public void EditDeleteSelected()
    {
        if (!EnsureSelectionBuffers()) return;
        UnionGraphicsBuffers(m_GpuSplatDeletedBuffer, m_GpuSplatSelectedBuffer);
        EditDeselectAll();
        UpdateEditCounts();
        if (m_EditDeletedSplats != 0)
            m_EditModified = true;
    }

    public void EditSelectAll()
    {
        if (!EnsureSelectionBuffers()) return;
        ClearGraphicsBuffer(m_GpuSplatSelectedBuffer, ~0u);
        UpdateEditCounts();
    }

    public void EditDeselectAll()
    {
        if (!EnsureSelectionBuffers()) return;
        ClearGraphicsBuffer(m_GpuSplatSelectedBuffer);
        UpdateEditCounts();
    }

    public void EditInvertSelection()
    {
        if (!EnsureSelectionBuffers()) return;
        InvertGraphicsBuffer(m_GpuSplatSelectedBuffer);
        UpdateEditCounts();
    }

    public bool EditExportData(GraphicsBuffer dstData)
    {
        if (!EnsureSelectionBuffers()) return false;
        
        var cmb = new CommandBuffer { name = "SplatExportData" };
        SetAssetDataOnCS(cmb, m_CSSplatUtilities, kKernelExportData);
        cmb.SetComputeIntParam(m_CSSplatUtilities, "_SplatCount", m_Asset.m_SplatCount);
        cmb.SetComputeBufferParam(m_CSSplatUtilities, kKernelExportData, "_SplatChunks", m_GpuChunks);
        cmb.SetComputeBufferParam(m_CSSplatUtilities, kKernelExportData, "_ExportBuffer", dstData);

        m_CSSplatUtilities.GetKernelThreadGroupSizes(kKernelExportData, out uint gsX, out _, out _);
        cmb.DispatchCompute(m_CSSplatUtilities, kKernelExportData, (m_Asset.m_SplatCount + (int)gsX - 1)/(int)gsX, 1, 1);
        Graphics.ExecuteCommandBuffer(cmb);
        cmb.Dispose();
        return true;
    }

    public GraphicsBuffer gpuSplatDeletedBuffer => m_GpuSplatDeletedBuffer;
}
