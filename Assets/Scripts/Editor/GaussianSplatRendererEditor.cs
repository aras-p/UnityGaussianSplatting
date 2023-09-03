using System;
using System.Dynamic;
using System.Linq;
using System.Reflection;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(GaussianSplatRenderer))]
[BurstCompile]
public class GaussianSplatRendererEditor : Editor
{
    static string[] kFieldNames = {
        "px", "py", "pz",
        "nx", "ny", "nz",
        "dc0r", "dc0g", "dc0b",
        "sh0r", "sh0g", "s0b",
        "sh1r", "sh1g", "sh1b",
        "sh2r", "sh2g", "sh2b",
        "sh3r", "sh3g", "sh3b",
        "sh4r", "sh4g", "sh4b",
        "sh5r", "sh5g", "sh5b",
        "sh6r", "sh6g", "sh6b",
        "sh7r", "sh7g", "sh7b",
        "sh8r", "sh8g", "sh8b",
        "sh9r", "sh9g", "sh9b",
        "sh10r", "sh10g", "sh10b",
        "sh11r", "sh11g", "sh11b",
        "sh12r", "sh12g", "sh12b",
        "sh13r", "sh13g", "sh13b",
        "sh14r", "sh14g", "sh14b",
        "op",
        "sx", "sy", "sz",
        "rw", "rx", "ry", "rz",
    };

    int m_CachedSplatCount;
    Vector2[] m_CachedDataRanges = new Vector2[kFieldNames.Length];
    Material m_Material;

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var gs = target as GaussianSplatRenderer;
        if (!gs)
            return;

        EditorGUILayout.Space();

        var splatCount = gs.splatCount;
        if (splatCount != 0)
        {
            using var disabled = new EditorGUI.DisabledScope(true);
            EditorGUILayout.IntField("Splats", splatCount);
            EditorGUILayout.Vector3Field("Center", gs.bounds.center);
            EditorGUILayout.Vector3Field("Extent", gs.bounds.extents);
        }

        CacheDataRanges();

        if (!m_Material)
            m_Material = new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/Scripts/Editor/DrawSplatDataRanges.shader"));
        if (!m_Material)
            return;

        float rowHeight = 12;
        var distRect = GUILayoutUtility.GetRect(100, kFieldNames.Length * rowHeight);
        var graphRect = new Rect(distRect.x+60, distRect.y, distRect.width-90, distRect.height);
        GUI.Box(graphRect, GUIContent.none);
        for (int bi = 0; bi < kFieldNames.Length; ++bi)
        {
            var rowRect = new Rect(distRect.x, distRect.y + bi * rowHeight, distRect.width, rowHeight);
            GUI.Label(new Rect(rowRect.x, rowRect.y, 30, rowRect.height), kFieldNames[bi], EditorStyles.miniLabel);
            GUI.Label(new Rect(rowRect.x+30, rowRect.y, 30, rowRect.height), m_CachedDataRanges[bi].x.ToString("F2"), EditorStyles.miniLabel);
            GUI.Label(new Rect(rowRect.xMax-30, rowRect.y, 30, rowRect.height), m_CachedDataRanges[bi].y.ToString("F2"), EditorStyles.miniLabel);
        }

        using (new GUI.ClipScope(graphRect))
        {
            if (Event.current.type == EventType.Repaint)
            {
                m_Material.SetBuffer("_InputData", gs.gpuSplatData);
                m_Material.SetVector("_Params", new Vector4(graphRect.width, graphRect.height, rowHeight, m_CachedDataRanges.Length));
                m_Material.SetFloatArray("_DataMin", m_CachedDataRanges.Select(f => f.x).ToArray());
                m_Material.SetFloatArray("_DataMax", m_CachedDataRanges.Select(f => f.y).ToArray());
                m_Material.SetPass(0);
                Graphics.DrawProceduralNow(MeshTopology.Points, splatCount, m_CachedDataRanges.Length);
            }
        }
    }

    unsafe void CacheDataRanges()
    {
        var gs = target as GaussianSplatRenderer;
        if (gs == null)
            m_CachedSplatCount = 0;

        if (m_CachedSplatCount == gs.splatCount)
            return;

        if (kFieldNames.Length != UnsafeUtility.SizeOf<GaussianSplatRenderer.InputSplat>() / 4)
            Debug.LogWarning("Field names array does not match expected size");

        m_CachedSplatCount = gs.splatCount;
        NativeArray<float> floatData =
            gs.splatData.Reinterpret<float>(UnsafeUtility.SizeOf<GaussianSplatRenderer.InputSplat>());
        // Doing this in Burst, since regular Mono for 3.6M splats takes 7.2 seconds, whereas Burst takes 0.2s...
        fixed (void* dst = m_CachedDataRanges)
        {
            CalcDataRanges(gs.splatCount, kFieldNames.Length, (float*)floatData.GetUnsafeReadOnlyPtr(), (float*)dst);
        }
    }

    [BurstCompile]
    static unsafe void CalcDataRanges(int splatCount, int fieldCount, float* data, float* ranges)
    {
        for (int i = 0; i < fieldCount; ++i)
        {
            ranges[i * 2 + 0] = float.PositiveInfinity;
            ranges[i * 2 + 1] = float.NegativeInfinity;
        }
        int idx = 0;
        for (int si = 0; si < splatCount; ++si)
        {
            for (int bi = 0; bi < fieldCount; ++bi)
            {
                float val = data[idx];
                ranges[bi * 2 + 0] = Mathf.Min(ranges[bi * 2 + 0], val);
                ranges[bi * 2 + 1] = Mathf.Max(ranges[bi * 2 + 1], val);
                ++idx;
            }
        }
    }
}
