using System;
using System.Collections.Generic;
using System.IO;
using TinyJson;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

[BurstCompile]
public class GaussianSplatRenderer : MonoBehaviour
{
    const string kPointCloudPly = "point_cloud/iteration_7000/point_cloud.ply";
    const string kPointCloud30kPly = "point_cloud/iteration_30000/point_cloud.ply";
    const string kCamerasJson = "cameras.json";

    [FolderPicker(nameKey:"PointCloudFolder", hasToContainFile:kPointCloudPly)]
    public string m_PointCloudFolder;

    [Tooltip("Use iteration_30000 point cloud if available. Otherwise uses iteration_7000.")]
    public bool m_Use30kVersion = false;

    [Space]
    [Tooltip("Gaussian splatting material")]
    public Material m_Material;

    [Tooltip("Gaussian splatting utilities compute shader")]
    public ComputeShader m_CSSplatUtilities;
    [Tooltip("'Island' bitonic sort compute shader")]
    [FormerlySerializedAs("m_CSGpuSort")]
    public ComputeShader m_CSIslandSort;
    [Tooltip("AMD FidelityFX sort compute shader")]
    public ComputeShader m_CSFfxSort;
    [Tooltip("Use AMD FidelityFX sorting when available, instead of the slower bitonic sort")]
    public bool m_PreferFfxSort = true; // use AMD FidelityFX sort if available (currently: DX12, Vulkan, Metal, but *not* DX11)

    [Tooltip("Reduce the number of splats used, by taking only 1/N of the total amount. Only for debugging!")]
    [Range(1,30)]
    public int m_ScaleDown = 10;

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

    int m_SplatCount;
    Bounds m_Bounds;
    NativeArray<InputSplat> m_SplatData;
    CameraData[] m_Cameras;

    GraphicsBuffer m_GpuData;
    GraphicsBuffer m_GpuPositions;
    GraphicsBuffer m_GpuSortDistances;
    GraphicsBuffer m_GpuSortKeys;

    IslandGPUSort m_SorterIsland;
    IslandGPUSort.Args m_SorterIslandArgs;
    FfxParallelSort m_SorterFfx;
    FfxParallelSort.Args m_SorterFfxArgs;

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
        if (m_Material == null || m_CSSplatUtilities == null || m_CSIslandSort == null)
        {
            Debug.LogWarning($"{nameof(GaussianSplatRenderer)} material/shader references are not set up");
            return;
        }

        m_Cameras = LoadJsonCamerasFile(m_PointCloudFolder);
        m_SplatData = LoadPLYSplatFile(m_PointCloudFolder, m_Use30kVersion);
        m_SplatCount = m_SplatData.Length / m_ScaleDown;
        if (m_SplatCount == 0)
        {
            Debug.LogWarning($"{nameof(GaussianSplatRenderer)} has no splats to render");
            return;
        }

        NativeArray<Vector3> inputPositions = new(m_SplatCount, Allocator.Temp);
        m_Bounds = new Bounds(m_SplatData[0].pos, Vector3.zero);
        for (var i = 0; i < m_SplatCount; ++i)
        {
            var pos = m_SplatData[i].pos;
            inputPositions[i] = pos;
            m_Bounds.Encapsulate(pos);
        }

        var bcen = m_Bounds.center;
        bcen.z *= -1;
        m_Bounds.center = bcen;

        m_GpuPositions = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SplatCount, 12);
        m_GpuPositions.SetData(inputPositions);
        inputPositions.Dispose();

        m_GpuData = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SplatCount, UnsafeUtility.SizeOf<InputSplat>());
        m_GpuData.SetData(m_SplatData, 0, 0, m_SplatCount);

        int splatCountNextPot = Mathf.NextPowerOfTwo(m_SplatCount);
        m_GpuSortDistances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splatCountNextPot, 4);
        m_GpuSortKeys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splatCountNextPot, 4);

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
    }

    void OnPreCullCamera(Camera cam)
    {
        if (m_GpuData == null)
            return;

        m_Material.SetBuffer("_InputPositions", m_GpuPositions);
        m_Material.SetBuffer("_DataBuffer", m_GpuData);
        m_Material.SetBuffer("_OrderBuffer", m_GpuSortKeys);

        SortPoints(cam);
        Graphics.DrawProcedural(m_Material, m_Bounds, MeshTopology.Triangles, 6, m_SplatCount, cam);
    }

    public void OnDisable()
    {
        Camera.onPreCull -= OnPreCullCamera;
        m_SplatData.Dispose();
        m_GpuData?.Dispose();
        m_GpuPositions?.Dispose();
        m_GpuSortDistances?.Dispose();
        m_GpuSortKeys?.Dispose();
        m_SorterFfxArgs.resources.Dispose();
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
        CommandBuffer cmd = new CommandBuffer {name = "GPUSort"};
        if (useFfx)
            m_SorterFfx.Dispatch(cmd, m_SorterFfxArgs);
        else
            m_SorterIsland.Dispatch(cmd, m_SorterIslandArgs);
        Graphics.ExecuteCommandBuffer(cmd);
        cmd.Dispose();
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
