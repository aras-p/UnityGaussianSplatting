using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

public class GaussianSplatRenderer : MonoBehaviour
{
    public TextAsset m_DataFile;
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
        if (m_DataFile == null || m_Material == null || m_CSSplatUtilities == null || m_CSGpuSort == null)
            return;
        if (UnsafeUtility.SizeOf<InputSplat>() != 248)
            throw new Exception("InputVertex size mismatch");
        var inputSplats = m_DataFile.GetData<InputSplat>();

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
}
