using System;
using System.IO;
using System.Linq;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public class GaussianSplatRenderer : MonoBehaviour
{
    const string kPointCloudPly = "point_cloud/iteration_7000/point_cloud.ply";

    [FolderPicker(kPointCloudPly)]
    public string m_PointCloudFolder;
    [Range(1,30)]
    public int m_ScaleDown = 10;
    public Material m_Material;
    public ComputeShader m_CSSplatUtilities;
    public ComputeShader m_CSGpuSort;

    // input file expected to be in this format
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
    
    private int m_SplatCount;
    private Bounds m_Bounds;
    
    private GraphicsBuffer m_GpuData;
    private GraphicsBuffer m_GpuPositions;
    private GraphicsBuffer m_GpuSortDistances;
    private GraphicsBuffer m_GpuSortKeys;

    private IslandGPUSort m_Sorter;
    private IslandGPUSort.Args m_SorterArgs;

    public void OnEnable()
    {
        if (m_Material == null || m_CSSplatUtilities == null || m_CSGpuSort == null)
            return;
        string plyPath = $"{m_PointCloudFolder}/{kPointCloudPly}";
        if (!File.Exists(plyPath))
            return;
        PLYFileReader.ReadFile(plyPath, out m_SplatCount, out int vertexStride, out var plyAttrNames, out var verticesRawData);
        if (UnsafeUtility.SizeOf<InputSplat>() != vertexStride)
            throw new Exception($"InputVertex size mismatch, we expect {UnsafeUtility.SizeOf<InputSplat>()} file has {vertexStride}");
        var inputSplats = verticesRawData.Reinterpret<InputSplat>(1);

        m_SplatCount = inputSplats.Length / m_ScaleDown;
        
        Debug.Log($"Input Splats: {m_SplatCount}");
        m_Bounds = new Bounds(inputSplats[0].pos, Vector3.zero);
        NativeArray<Vector3> inputPositions = new NativeArray<Vector3>(m_SplatCount, Allocator.Temp);
        for (var i = 0; i < m_SplatCount; ++i)
        {
            var pos = inputSplats[i].pos;
            inputPositions[i] = pos;
            m_Bounds.Encapsulate(pos);
        }

        m_GpuPositions = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SplatCount, 12);
        m_GpuPositions.SetData(inputPositions);
        inputPositions.Dispose();

        m_GpuData = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SplatCount, UnsafeUtility.SizeOf<InputSplat>());
        m_GpuData.SetData(inputSplats, 0, 0, m_SplatCount);

        m_GpuSortDistances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SplatCount, 4);
        m_GpuSortKeys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SplatCount, 4);
        
        m_Material.SetBuffer("_DataBuffer", m_GpuData);
        m_Material.SetBuffer("_OrderBuffer", m_GpuSortKeys);

        m_Sorter = new IslandGPUSort(m_CSGpuSort);
        m_SorterArgs.inputKeys = m_GpuSortDistances;
        m_SorterArgs.inputValues = m_GpuSortKeys;
        m_SorterArgs.count = (uint)m_SplatCount;
        m_SorterArgs.resources = IslandGPUSort.SupportResources.Load(m_SplatCount);
        m_Material.SetBuffer("_OrderBuffer", m_SorterArgs.resources.sortBufferValues);

        verticesRawData.Dispose();
    }

    public void OnDisable()
    {
        m_GpuData?.Dispose();
        m_GpuPositions?.Dispose();
        m_GpuSortDistances?.Dispose();
        m_GpuSortKeys?.Dispose();
        m_SorterArgs.resources.Dispose();
    }

    public void Update()
    {
        if (m_GpuData == null)
            return;

        SortPoints();
        Graphics.DrawProcedural(m_Material, m_Bounds, MeshTopology.Triangles, 36, m_SplatCount);
    }

    void SortPoints()
    {
        var cam = Camera.current;
        if (cam == null)
            cam = Camera.main;
        if (cam == null)
            return;

        // calculate distance to the camera for each splat
        m_CSSplatUtilities.SetBuffer(0, "_InputPositions", m_GpuPositions);
        m_CSSplatUtilities.SetBuffer(0, "_SplatSortDistances", m_GpuSortDistances);
        m_CSSplatUtilities.SetBuffer(0, "_SplatSortKeys", m_GpuSortKeys);
        m_CSSplatUtilities.SetMatrix("_WorldToCameraMatrix", cam.worldToCameraMatrix);
        m_CSSplatUtilities.SetInt("_SplatCount", m_SplatCount);
        m_CSSplatUtilities.GetKernelThreadGroupSizes(0, out uint gsX, out uint gsY, out uint gsZ);
        m_CSSplatUtilities.Dispatch(0, (m_SplatCount + (int)gsX - 1)/(int)gsX, 1, 1);

        // sort the splats
        CommandBuffer cmd = new CommandBuffer {name = "GPUSort"};
        m_Sorter.Dispatch(cmd, m_SorterArgs);
        Graphics.ExecuteCommandBuffer(cmd);
    }

    [Serializable]
    public class JsonCamera
    {
        public int id;
        public string img_name;
        public int width;
        public int height;
        public float[] position;
        public float[] rotx;
        public float[] roty;
        public float[] rotz;
        public float fx;
        public float fy;
    }

    [Serializable]
    public class JsonCameras
    {
        public JsonCamera[] cameras;
    }

    [MenuItem("Tools/Import Cameras")]
    public static void ImportCameras()
    {
        var existingCams = FindObjectsOfType<GameObject>()
            .Where(c => c.name.StartsWith("ImportedCam-", StringComparison.Ordinal));
        Debug.Log($"Existing cams: {existingCams.Count()}");
        foreach (var cam in existingCams)
            DestroyImmediate(cam);

        string json = System.IO.File.ReadAllText("Assets/Models/bicycle_cameras.json");
        // JsonUtility does not support 2D arrays, so mogrify that into something else
        string num = "([\\-\\d\\.]+)";
        string vec = $"\\[{num},\\s*{num},\\s*{num}\\]";
        json = System.Text.RegularExpressions.Regex.Replace(json,
            $"\"rotation\": \\[{vec},\\s*{vec},\\s*{vec}\\]",
            "\"rotx\":[$1,$2,$3], \"roty\":[$4,$5,$6], \"rotz\":[$7,$8,$9]"
        );
        json = $"{{ \"cameras\": {json} }}";
        var cameras = JsonUtility.FromJson<JsonCameras>(json);
        Debug.Log($"Json had {cameras.cameras.Length} cameras");

        foreach (var data in cameras.cameras)
        {
            var go = new GameObject($"ImportedCam-{data.id}", typeof(Camera));
            var cam = go.GetComponent<Camera>();
            cam.enabled = false;
            cam.nearClipPlane = 0.5f;
            cam.farClipPlane = 10;
            cam.fieldOfView = 25;
            var tr = go.transform;
            var pos = new Vector3(data.position[0], data.position[1], data.position[2]);

            // the matrix is a "view matrix", not "camera matrix" lol
            var axisx = new Vector3(data.rotx[0], data.roty[0], data.rotz[0]);
            var axisy = new Vector3(data.rotx[1], data.roty[1], data.rotz[1]);
            var axisz = new Vector3(data.rotx[2], data.roty[2], data.rotz[2]);

            pos.z *= -1;
            axisy *= -1;
            axisx.z *= -1;
            axisy.z *= -1;
            axisz.z *= -1;

            tr.position = pos;
            tr.LookAt(tr.position + axisz, axisy);
        }
    }
}
