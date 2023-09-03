using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[CustomEditor(typeof(GaussianSplatRenderer))]
[BurstCompile]
public class GaussianSplatRendererEditor : Editor
{
    const int kRowHeight = 12;
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

    Vector2[] m_CachedDataRanges;
    Texture2D m_StatsTexture;

    public void OnDestroy()
    {
        if (m_StatsTexture) DestroyImmediate(m_StatsTexture);
    }

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

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Calc Stats"))
            CalcStats(gs.pointCloudFolder);
        if (GUILayout.Button("Clear Stats", GUILayout.ExpandWidth(false)))
            ClearStats();
        GUILayout.EndHorizontal();

        if (m_StatsTexture && m_CachedDataRanges != null)
        {
            var distRect = GUILayoutUtility.GetRect(100, kFieldNames.Length * kRowHeight);
            var graphRect = new Rect(distRect.x + 60, distRect.y, distRect.width - 90, distRect.height);
            GUI.Box(graphRect, GUIContent.none);
            for (int bi = 0; bi < kFieldNames.Length; ++bi)
            {
                var rowRect = new Rect(distRect.x, distRect.y + bi * kRowHeight, distRect.width, kRowHeight);
                GUI.Label(new Rect(rowRect.x, rowRect.y, 30, rowRect.height), kFieldNames[bi], EditorStyles.miniLabel);
                GUI.Label(new Rect(rowRect.x + 30, rowRect.y, 30, rowRect.height),
                    m_CachedDataRanges[bi].x.ToString("F2"), EditorStyles.miniLabel);
                GUI.Label(new Rect(rowRect.xMax - 30, rowRect.y, 30, rowRect.height),
                    m_CachedDataRanges[bi].y.ToString("F2"), EditorStyles.miniLabel);
            }
            GUI.DrawTexture(graphRect, m_StatsTexture, ScaleMode.StretchToFill);
        }
    }

    [BurstCompile]
    struct CalcStatsJob : IJobParallelFor
    {
        public int pixelsWidth;
        public int pixelsHeight;
        [NativeDisableParallelForRestriction] public NativeArray<Color32> pixels;
        public NativeArray<Vector2> ranges;
        [ReadOnly] public NativeArray<float> data;
        public int itemCount;
        public int itemStrideInFloats;

        public void Execute(int fieldIndex)
        {
            // find min/max
            Vector2 range = new Vector2(float.PositiveInfinity, float.NegativeInfinity);
            int idx = fieldIndex;
            for (int si = 0; si < itemCount; ++si)
            {
                float val = data[idx];
                range.x = math.min(range.x, val);
                range.y = math.max(range.y, val);
                idx += itemStrideInFloats;
            }
            ranges[fieldIndex] = range;

            // fill texture with value distribution over the range
            idx = fieldIndex;
            for (int si = 0; si < itemCount; ++si)
            {
                float val = data[idx];
                val = math.unlerp(range.x, range.y, val);
                val = math.saturate(val);
                int px = (int) math.floor(val * pixelsWidth);
                int py = pixelsHeight - 1 - (fieldIndex * kRowHeight + 1 + (si % (kRowHeight - 2)));
                int pidx = py * pixelsWidth + px;

                Color32 col = pixels[pidx];
                col.r = (byte)math.min(255, col.r + 3);
                col.g = (byte)math.min(255, col.g + 2);
                col.b = (byte)math.min(255, col.b + 1);
                col.a = 255;
                pixels[pidx] = col;

                idx += itemStrideInFloats;
            }
        }
    }

    void ClearStats()
    {
        m_CachedDataRanges = null;
        if (m_StatsTexture)
            DestroyImmediate(m_StatsTexture);
    }
    void CalcStats(string pointCloudFolder)
    {
        ClearStats();
        NativeArray<GaussianSplatRenderer.InputSplat> splats = GaussianSplatRenderer.LoadPLYSplatFile(pointCloudFolder);
        if (!splats.IsCreated)
            return;

        int itemSizeBytes = UnsafeUtility.SizeOf<GaussianSplatRenderer.InputSplat>();
        int fieldCount = itemSizeBytes / 4;

        if (!m_StatsTexture)
            m_StatsTexture = new Texture2D(512, fieldCount * kRowHeight, GraphicsFormat.R8G8B8A8_UNorm, TextureCreationFlags.None);
        NativeArray<Color32> statsPixels = new(m_StatsTexture.width * m_StatsTexture.height, Allocator.TempJob);
        NativeArray<Vector2> statsRanges = new(fieldCount, Allocator.TempJob);

        CalcStatsJob job;
        job.pixelsWidth = m_StatsTexture.width;
        job.pixelsHeight = m_StatsTexture.height;
        job.pixels = statsPixels;
        job.ranges = statsRanges;
        job.data = splats.Reinterpret<float>(itemSizeBytes);
        job.itemCount = splats.Length;
        job.itemStrideInFloats = fieldCount;
        job.Schedule(fieldCount, 1).Complete();

        m_StatsTexture.SetPixelData(statsPixels, 0);
        m_StatsTexture.Apply(false);
        m_CachedDataRanges = new Vector2[fieldCount];
        for (int i = 0; i < fieldCount; ++i)
            m_CachedDataRanges[i] = statsRanges[i];

        statsPixels.Dispose();
        statsRanges.Dispose();
        splats.Dispose();
    }
}
