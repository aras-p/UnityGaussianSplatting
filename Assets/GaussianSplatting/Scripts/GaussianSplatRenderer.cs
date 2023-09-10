using System;
using System.Collections.Generic;
using System.IO;
using TinyJson;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

[BurstCompile]
public class GaussianSplatRenderer : MonoBehaviour
{
    const string kPointCloudPly = "point_cloud/iteration_7000/point_cloud.ply";
    const string kPointCloud30kPly = "point_cloud/iteration_30000/point_cloud.ply";
    const string kCamerasJson = "cameras.json";

    public enum RenderMode
    {
        Splats,
        DebugPoints,
        DebugPointIndices,
        DebugBoxes,
        DebugChunkBounds,
    }

    public enum DisplayDataMode
    {
        None = 0,
        Position = 1,
        Scale = 2,
        Rotation = 3,
        Color = 4,
        Opacity = 5,
        SH1, SH2, SH3, SH4, SH5, SH6, SH7, SH8, SH9, SH10, SH11, SH12, SH13, SH14, SH15,
    }

    [Header("Data File")]

    [FolderPicker(nameKey:"PointCloudFolder", hasToContainFile:kPointCloudPly)]
    public string m_PointCloudFolder;
    [Tooltip("Use iteration_30000 point cloud if available. Otherwise uses iteration_7000.")]
    public bool m_Use30kVersion = false;

    [Header("Render Options")]

    [Range(0.1f, 2.0f)] [Tooltip("Additional scaling factor for the splats")]
    public float m_SplatScale = 1.0f;
    [Range(0, 3)] [Tooltip("Spherical Harmonics order to use")]
    public int m_SHOrder = 3;
    [Range(1,30)] [Tooltip("Sort splats only every N frames")]
    public int m_SortNthFrame = 1;

    [Header("Debugging Tweaks")]

    public RenderMode m_RenderMode = RenderMode.Splats;
    [Range(1.0f,15.0f)]
    public float m_PointDisplaySize = 3.0f;
    public DisplayDataMode m_DisplayData = DisplayDataMode.None;

    [Tooltip("Use AMD FidelityFX sorting when available, instead of the slower bitonic sort")]
    public bool m_PreferFfxSort = true; // use AMD FidelityFX sort if available (currently: DX12, Vulkan, Metal, but *not* DX11)

    [Tooltip("Reduce the number of splats used, by taking only 1/N of the total amount. Only for debugging!")]
    [Range(1,30)]
    public int m_ScaleDown = 10;

    [Header("Resources")]

    public Shader m_ShaderSplats;
    public Shader m_ShaderComposite;
    public Shader m_ShaderDebugPoints;
    public Shader m_ShaderDebugBoxes;
    public Shader m_ShaderDebugData;
    [Tooltip("Gaussian splatting utilities compute shader")]
    public ComputeShader m_CSSplatUtilities;
    [Tooltip("'Island' bitonic sort compute shader")]
    [FormerlySerializedAs("m_CSGpuSort")]
    public ComputeShader m_CSIslandSort;
    [Tooltip("AMD FidelityFX sort compute shader")]
    public ComputeShader m_CSFfxSort;


    // input file splat data is expected to be in this format
    public struct InputSplat
    {
        public Vector3 pos;
        public Vector3 nor;
        public Vector3 dc0;
        public Vector3 sh0, sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, sh10, sh11, sh12, sh13, sh14;
        public float opacity;
        public Vector3 scale;
        public Quaternion rot;
    }

    public struct CameraData
    {
        public Vector3 pos;
        public Vector3 axisX, axisY, axisZ;
        public float fov;
    }

    public struct ChunkData
    {
        public float3 bmin;
        public float3 bmax;
    }

    const int kChunkSize = 256;

    int m_SplatCount;
    Bounds m_Bounds;
    NativeArray<InputSplat> m_SplatData;
    CameraData[] m_Cameras;

    GraphicsBuffer m_GpuData;
    GraphicsBuffer m_GpuPositions;
    GraphicsBuffer m_GpuSortDistances;
    GraphicsBuffer m_GpuSortKeys;
    GraphicsBuffer m_GpuChunks;

    IslandGPUSort m_SorterIsland;
    IslandGPUSort.Args m_SorterIslandArgs;
    FfxParallelSort m_SorterFfx;
    FfxParallelSort.Args m_SorterFfxArgs;

    CommandBuffer m_RenderCommandBuffer;
    readonly HashSet<Camera> m_CameraCommandBuffersDone = new();

    Material m_MatSplats;
    Material m_MatComposite;
    Material m_MatDebugPoints;
    Material m_MatDebugBoxes;
    Material m_MatDebugData;

    int m_FrameCounter;

    public string pointCloudFolder => m_PointCloudFolder;
    public int splatCount => m_SplatCount;
    public Bounds bounds => m_Bounds;
    public NativeArray<InputSplat> splatData => m_SplatData;
    public GraphicsBuffer gpuSplatData => m_GpuData;
    public CameraData[] cameras => m_Cameras;

    public static unsafe NativeArray<InputSplat> LoadPLYSplatFile(string folder, bool use30k)
    {
        NativeArray<InputSplat> data = default;
        string plyPath = $"{folder}/{(use30k ? kPointCloud30kPly : kPointCloudPly)}";
        if (!File.Exists(plyPath))
        {
            plyPath = $"{folder}/{kPointCloudPly}";
            if (!File.Exists(plyPath))
                return data;
        }

        int splatCount = 0;
        PLYFileReader.ReadFile(plyPath, out splatCount, out int vertexStride, out var plyAttrNames, out var verticesRawData);
        if (UnsafeUtility.SizeOf<InputSplat>() != vertexStride)
            throw new Exception($"InputVertex size mismatch, we expect {UnsafeUtility.SizeOf<InputSplat>()} file has {vertexStride}");

        // reorder SHs
        NativeArray<float> floatData = verticesRawData.Reinterpret<float>(1);
        ReorderSHs(splatCount, (float*)floatData.GetUnsafePtr());


        return verticesRawData.Reinterpret<InputSplat>(1);
    }

    [BurstCompile]
    static unsafe void ReorderSHs(int splatCount, float* data)
    {
        int splatStride = UnsafeUtility.SizeOf<InputSplat>() / 4;
        int shStartOffset = 9, shCount = 15;
        float* tmp = stackalloc float[shCount * 3];
        int idx = shStartOffset;
        for (int i = 0; i < splatCount; ++i)
        {
            for (int j = 0; j < shCount; ++j)
            {
                tmp[j * 3 + 0] = data[idx + j];
                tmp[j * 3 + 1] = data[idx + j + shCount];
                tmp[j * 3 + 2] = data[idx + j + shCount * 2];
            }

            for (int j = 0; j < shCount * 3; ++j)
            {
                data[idx + j] = tmp[j];
            }

            idx += splatStride;
        }
    }

    static CameraData[] LoadJsonCamerasFile(string folder)
    {
        string path = $"{folder}/{kCamerasJson}";
        if (!File.Exists(path))
            return null;

        string json = File.ReadAllText(path);
        var jsonCameras = JSONParser.FromJson<List<JsonCamera>>(json);
        if (jsonCameras == null || jsonCameras.Count == 0)
            return null;

        var result = new CameraData[jsonCameras.Count];
        for (var camIndex = 0; camIndex < jsonCameras.Count; camIndex++)
        {
            var jsonCam = jsonCameras[camIndex];
            var pos = new Vector3(jsonCam.position[0], jsonCam.position[1], jsonCam.position[2]);
            // the matrix is a "view matrix", not "camera matrix" lol
            var axisx = new Vector3(jsonCam.rotation[0][0], jsonCam.rotation[1][0], jsonCam.rotation[2][0]);
            var axisy = new Vector3(jsonCam.rotation[0][1], jsonCam.rotation[1][1], jsonCam.rotation[2][1]);
            var axisz = new Vector3(jsonCam.rotation[0][2], jsonCam.rotation[1][2], jsonCam.rotation[2][2]);

            pos.z *= -1;
            axisy *= -1;
            axisx.z *= -1;
            axisy.z *= -1;
            axisz.z *= -1;

            var cam = new CameraData
            {
                pos = pos,
                axisX = axisx,
                axisY = axisy,
                axisZ = axisz,
                fov = 25 //@TODO
            };
            result[camIndex] = cam;
        }

        return result;
    }

    public void OnEnable()
    {
        Camera.onPreCull += OnPreCullCamera;

        m_Cameras = null;
        m_FrameCounter = 0;
        if (m_ShaderSplats == null || m_ShaderComposite == null || m_ShaderDebugPoints == null || m_ShaderDebugBoxes == null || m_ShaderDebugData == null || m_CSSplatUtilities == null || m_CSIslandSort == null)
        {
            Debug.LogWarning($"{nameof(GaussianSplatRenderer)} shader references are not set up", this);
            return;
        }

        m_MatSplats = new Material(m_ShaderSplats) {name = "GaussianSplats"};
        m_MatComposite = new Material(m_ShaderComposite) {name = "GaussianClearDstAlpha"};
        m_MatDebugPoints = new Material(m_ShaderDebugPoints) {name = "GaussianDebugPoints"};
        m_MatDebugBoxes = new Material(m_ShaderDebugBoxes) {name = "GaussianDebugBoxes"};
        m_MatDebugData = new Material(m_ShaderDebugData) {name = "GaussianDebugData"};

        m_Cameras = LoadJsonCamerasFile(m_PointCloudFolder);
        m_SplatData = LoadPLYSplatFile(m_PointCloudFolder, m_Use30kVersion);
        m_SplatCount = m_SplatData.Length / m_ScaleDown;
        if (m_SplatCount == 0)
        {
            Debug.LogWarning($"{nameof(GaussianSplatRenderer)} has no splats to render");
            return;
        }

        m_Bounds = new Bounds(m_SplatData[0].pos, Vector3.zero);
        for (var i = 0; i < m_SplatCount; ++i)
        {
            var pos = m_SplatData[i].pos;
            m_Bounds.Encapsulate(pos);
        }
        var bcen = m_Bounds.center;
        bcen.z *= -1;
        m_Bounds.center = bcen;

        m_GpuPositions = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SplatCount, 12) { name = "GaussianSplatPositions" };
        m_GpuData = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SplatCount, UnsafeUtility.SizeOf<InputSplat>()) { name = "GaussianSplatData" };
        m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, (m_SplatCount + kChunkSize - 1) / kChunkSize, UnsafeUtility.SizeOf<ChunkData>()) { name = "GaussianChunkData" };

        UpdateGPUBuffers();

        int splatCountNextPot = Mathf.NextPowerOfTwo(m_SplatCount);
        m_GpuSortDistances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splatCountNextPot, 4) { name = "GaussianSplatSortDistances" };
        m_GpuSortKeys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splatCountNextPot, 4) { name = "GaussianSplatSortIndices" };

        // init keys buffer to splat indices
        m_CSSplatUtilities.SetBuffer(0, "_SplatSortKeys", m_GpuSortKeys);
        m_CSSplatUtilities.SetInt("_SplatCountPOT", m_GpuSortDistances.count);
        m_CSSplatUtilities.GetKernelThreadGroupSizes(0, out uint gsX, out uint gsY, out uint gsZ);
        m_CSSplatUtilities.Dispatch(0, (m_GpuSortDistances.count + (int)gsX - 1)/(int)gsX, 1, 1);

        m_SorterIsland = new IslandGPUSort(m_CSIslandSort);
        m_SorterIslandArgs.keys = m_GpuSortDistances;
        m_SorterIslandArgs.values = m_GpuSortKeys;
        m_SorterIslandArgs.count = (uint)splatCountNextPot;

        m_SorterFfx = new FfxParallelSort(m_CSFfxSort);
        m_SorterFfxArgs.inputKeys = m_GpuSortDistances;
        m_SorterFfxArgs.inputValues = m_GpuSortKeys;
        m_SorterFfxArgs.count = (uint) m_SplatCount;
        if (m_SorterFfx.Valid)
            m_SorterFfxArgs.resources = FfxParallelSort.SupportResources.Load((uint)m_SplatCount);

        m_RenderCommandBuffer = new CommandBuffer {name = "GaussianRender"};
    }

    public void UpdateGPUBuffers()
    {
        NativeArray<Vector3> inputPositions = new(m_SplatCount, Allocator.Temp);
        NativeArray<ChunkData> chunks = new(m_GpuChunks.count, Allocator.Temp);
        ChunkData chunk = default;
        int chunkIndex = 0;
        for (var i = 0; i < m_SplatCount; ++i)
        {
            var pos = m_SplatData[i].pos;
            inputPositions[i] = pos;

            if (i % kChunkSize == 0)
            {
                chunk.bmin = float.PositiveInfinity;
                chunk.bmax = float.NegativeInfinity;
            }

            chunk.bmin = math.min(chunk.bmin, pos);
            chunk.bmax = math.max(chunk.bmax, pos);
            if ((i + 1) % kChunkSize == 0)
            {
                chunks[chunkIndex] = chunk;
                ++chunkIndex;
            }
        }

        if (chunkIndex < chunks.Length) // store last perhaps not full chunk
            chunks[chunkIndex] = chunk;
        m_GpuPositions.SetData(inputPositions);
        inputPositions.Dispose();

        m_GpuChunks.SetData(chunks);
        chunks.Dispose();

        m_GpuData.SetData(m_SplatData, 0, 0, m_SplatCount);
    }

    void OnPreCullCamera(Camera cam)
    {
        m_RenderCommandBuffer.Clear();

        if (m_GpuData == null)
            return;

        Material displayMat = m_RenderMode switch
        {
            RenderMode.DebugPoints => m_MatDebugPoints,
            RenderMode.DebugPointIndices => m_MatDebugPoints,
            RenderMode.DebugBoxes => m_MatDebugBoxes,
            RenderMode.DebugChunkBounds => m_MatDebugBoxes,
            _ => m_MatSplats
        };
        if (displayMat == null)
            return;

        if (!m_CameraCommandBuffersDone.Contains(cam))
        {
            cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, m_RenderCommandBuffer);
            m_CameraCommandBuffersDone.Add(cam);
        }

        displayMat.SetBuffer("_InputPositions", m_GpuPositions);
        displayMat.SetBuffer("_DataBuffer", m_GpuData);
        displayMat.SetBuffer("_ChunkBuffer", m_GpuChunks);
        displayMat.SetBuffer("_OrderBuffer", m_GpuSortKeys);
        displayMat.SetFloat("_SplatScale", m_SplatScale);
        displayMat.SetFloat("_SplatSize", m_PointDisplaySize);
        displayMat.SetInteger("_SplatCount", m_SplatCount);
        displayMat.SetInteger("_ChunkCount", m_GpuChunks.count);
        displayMat.SetInteger("_SHOrder", m_SHOrder);
        displayMat.SetInteger("_DisplayIndex", m_RenderMode == RenderMode.DebugPointIndices ? 1 : 0);
        bool displayAsLine = false; //m_RenderMode == RenderMode.DebugPointIndices;
        displayMat.SetInteger("_DisplayLine", displayAsLine ? 1 : 0);
        displayMat.SetInteger("_DisplayChunks", m_RenderMode == RenderMode.DebugChunkBounds ? 1 : 0);

        if (m_FrameCounter % m_SortNthFrame == 0)
            SortPoints(cam);
        ++m_FrameCounter;

        int vertexCount = 6;
        int instanceCount = m_SplatCount;
        MeshTopology topology = MeshTopology.Triangles;
        if (m_RenderMode is RenderMode.DebugBoxes or RenderMode.DebugChunkBounds)
            vertexCount = 36;
        if (displayAsLine)
        {
            topology = MeshTopology.LineStrip;
            vertexCount = m_SplatCount;
            instanceCount = 1;
        }
        if (m_RenderMode == RenderMode.DebugChunkBounds)
            instanceCount = m_GpuChunks.count;

        int rtNameID = Shader.PropertyToID("_GaussianSplatRT");
        m_RenderCommandBuffer.GetTemporaryRT(rtNameID, -1, -1, 0, FilterMode.Point, GraphicsFormat.R16G16B16A16_SFloat);
        m_RenderCommandBuffer.SetRenderTarget(rtNameID, BuiltinRenderTextureType.CurrentActive);
        m_RenderCommandBuffer.ClearRenderTarget(RTClearFlags.Color, new Color(0,0,0,0), 0, 0);
        m_RenderCommandBuffer.DrawProcedural(Matrix4x4.identity, displayMat, 0, topology, vertexCount, instanceCount);
        m_RenderCommandBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
        m_RenderCommandBuffer.DrawProcedural(Matrix4x4.identity, m_MatComposite, 0, MeshTopology.Triangles, 6, 1);
        m_RenderCommandBuffer.ReleaseTemporaryRT(rtNameID);


        if (m_DisplayData != DisplayDataMode.None)
        {
            m_MatDebugData.SetBuffer("_DataBuffer", m_GpuData);
            m_MatDebugData.SetInteger("_SplatCount", m_SplatCount);
            m_MatDebugData.SetInteger("_DisplayMode", (int)m_DisplayData);
            m_MatDebugData.SetVector("_BoundsMin", m_Bounds.min);
            m_MatDebugData.SetVector("_BoundsMax", m_Bounds.max);
            Graphics.DrawProcedural(m_MatDebugData, new Bounds(cam.transform.position, Vector3.one * 1000.0f), MeshTopology.Triangles, 6, 1, cam);
        }
    }

    public void OnDisable()
    {
        Camera.onPreCull -= OnPreCullCamera;

        m_CameraCommandBuffersDone?.Clear();
        m_RenderCommandBuffer?.Clear();

        m_SplatData.Dispose();
        m_GpuData?.Dispose();
        m_GpuChunks?.Dispose();
        m_GpuPositions?.Dispose();
        m_GpuSortDistances?.Dispose();
        m_GpuSortKeys?.Dispose();
        m_SorterFfxArgs.resources.Dispose();

        DestroyImmediate(m_MatSplats);
        DestroyImmediate(m_MatComposite);
        DestroyImmediate(m_MatDebugPoints);
        DestroyImmediate(m_MatDebugBoxes);
        DestroyImmediate(m_MatDebugData);
    }

    void SortPoints(Camera cam)
    {
        bool useFfx = m_PreferFfxSort && m_SorterFfx.Valid;
        Matrix4x4 worldToCamMatrix = cam.worldToCameraMatrix;
        if (useFfx)
        {
            worldToCamMatrix.m20 *= -1;
            worldToCamMatrix.m21 *= -1;
            worldToCamMatrix.m22 *= -1;
        }

        // calculate distance to the camera for each splat
        m_CSSplatUtilities.SetBuffer(1, "_InputPositions", m_GpuPositions);
        m_CSSplatUtilities.SetBuffer(1, "_SplatSortDistances", m_GpuSortDistances);
        m_CSSplatUtilities.SetBuffer(1, "_SplatSortKeys", m_GpuSortKeys);
        m_CSSplatUtilities.SetMatrix("_WorldToCameraMatrix", worldToCamMatrix);
        m_CSSplatUtilities.SetInt("_SplatCount", m_SplatCount);
        m_CSSplatUtilities.SetInt("_SplatCountPOT", m_GpuSortDistances.count);
        m_CSSplatUtilities.GetKernelThreadGroupSizes(1, out uint gsX, out uint gsY, out uint gsZ);
        m_CSSplatUtilities.Dispatch(1, (m_GpuSortDistances.count + (int)gsX - 1)/(int)gsX, 1, 1);

        // sort the splats
        if (useFfx)
            m_SorterFfx.Dispatch(m_RenderCommandBuffer, m_SorterFfxArgs);
        else
            m_SorterIsland.Dispatch(m_RenderCommandBuffer, m_SorterIslandArgs);
    }

    [Serializable]
    public class JsonCamera
    {
        public int id;
        public string img_name;
        public int width;
        public int height;
        public float[] position;
        public float[][] rotation;
        public float fx;
        public float fy;
    }
}
