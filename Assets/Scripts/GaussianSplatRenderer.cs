using System;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

public class GaussianSplatRenderer : MonoBehaviour
{
    public TextAsset m_DataFile;
    public Material m_Material;

    // input file expected to be in this format
    struct InputVertex
    {
        public Vector3 pos;
        public Vector3 nor;
        public Vector3 dc0;
        public Vector3 sh0, sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, sh10, sh11, sh12, sh13, sh14;
        public float opacity;
        public Vector3 scale;
        public Quaternion rot;
    }

    private int m_VertexCount;
    private Bounds m_Bounds;
    private GraphicsBuffer m_DataBuffer;

    public void Start()
    {
        var inputVerts = m_DataFile.GetData<InputVertex>();
        m_VertexCount = inputVerts.Length;
        
        Debug.Log($"Input Verts: {m_VertexCount}");
        m_Bounds = new Bounds(inputVerts[0].pos, Vector3.zero);
        for (var i = 0; i < m_VertexCount; ++i)
        {
            m_Bounds.Encapsulate(inputVerts[i].pos);
        }

        m_DataBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_VertexCount,
            UnsafeUtility.SizeOf<InputVertex>());
        m_DataBuffer.SetData(inputVerts);
        m_Material.SetBuffer("_DataBuffer", m_DataBuffer);
    }

    public void OnDestroy()
    {
        m_DataBuffer.Dispose();
    }

    public void Update()
    {
        Graphics.DrawProcedural(m_Material, m_Bounds, MeshTopology.Points, 1, m_VertexCount);
    }
}
