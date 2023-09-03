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
    Vector2[] m_CachedDataRanges;

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var gs = target as GaussianSplatRenderer;
        if (!gs)
            return;
        var splatCount = gs.splatCount;
        if (splatCount == 0)
            return;

        EditorGUILayout.Space();
        using var disabled = new EditorGUI.DisabledScope(true);
        EditorGUILayout.IntField("Splats", splatCount);
        EditorGUILayout.Vector3Field("Center", gs.bounds.center);
        EditorGUILayout.Vector3Field("Extent", gs.bounds.extents);

        CacheDataRanges();
        if (m_CachedDataRanges != null)
        {
            for (int bi = 0; bi < kFieldNames.Length; ++bi)
            {
                EditorGUILayout.Vector2Field(kFieldNames[bi], m_CachedDataRanges[bi]);
            }
        }
    }

    unsafe void CacheDataRanges()
    {
        var gs = target as GaussianSplatRenderer;
        if (gs == null)
        {
            m_CachedSplatCount = 0;
            m_CachedDataRanges = null;
        }

        if (m_CachedDataRanges != null && m_CachedSplatCount == gs.splatCount)
            return;

        if (kFieldNames.Length != UnsafeUtility.SizeOf<GaussianSplatRenderer.InputSplat>() / 4)
            Debug.LogWarning("Field names array does not match expected size");

        m_CachedSplatCount = gs.splatCount;
        NativeArray<float> floatData =
            gs.splatData.Reinterpret<float>(UnsafeUtility.SizeOf<GaussianSplatRenderer.InputSplat>());
        m_CachedDataRanges = new Vector2[kFieldNames.Length];

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
