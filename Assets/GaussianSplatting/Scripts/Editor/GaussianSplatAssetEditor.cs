using Unity.Burst;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;

[CustomEditor(typeof(GaussianSplatAsset))]
[BurstCompile]
public class GaussianSplatAssetEditor : Editor
{
    /*
    const int kRowHeight = 12;
    static string[] kFieldNames = {
        "px", "py", "pz", // 0
        "nx", "ny", "nz", // 3
        "dc0r", "dc0g", "dc0b", // 6
        "sh0r", "sh0g", "sh0b", // 9
        "sh1r", "sh1g", "sh1b", // 12
        "sh2r", "sh2g", "sh2b", // 15
        "sh3r", "sh3g", "sh3b", // 18
        "sh4r", "sh4g", "sh4b", // 21
        "sh5r", "sh5g", "sh5b", // 24
        "sh6r", "sh6g", "sh6b", // 27
        "sh7r", "sh7g", "sh7b", // 30
        "sh8r", "sh8g", "sh8b", // 33
        "sh9r", "sh9g", "sh9b", // 36
        "shAr", "shAg", "shAb", // 39
        "shBr", "shBg", "shBb", // 42
        "shCr", "shCg", "shCb", // 45
        "shDr", "shDg", "shDb", // 48
        "shEr", "shEg", "shEb", // 51
        "op", // 54
        "sx", "sy", "sz", // 55
        "rw", "rx", "ry", "rz", // 58
    };

    Vector2[] m_CachedDataRanges;
    Texture2D m_StatsTexture;
    */

    public void OnDestroy()
    {
        //if (m_StatsTexture) DestroyImmediate(m_StatsTexture);
    }

    static long GetTextureSize(Texture2D tex)
    {
        if (tex == null)
            return 0;
        return GraphicsFormatUtility.ComputeMipmapSize(tex.width, tex.height, tex.graphicsFormat);
    }

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var gs = target as GaussianSplatAsset;
        if (!gs)
            return;

        EditorGUILayout.Space();

        var splatCount = gs.m_SplatCount;
        {
            using var _ = new EditorGUI.DisabledScope(true);
            EditorGUILayout.IntField("Splats", splatCount);

            long sizePos = GetTextureSize(gs.GetTex(GaussianSplatAsset.TexType.Pos));
            long sizeRot = GetTextureSize(gs.GetTex(GaussianSplatAsset.TexType.Rot));
            long sizeScl = GetTextureSize(gs.GetTex(GaussianSplatAsset.TexType.Scl));
            long sizeCol = GetTextureSize(gs.GetTex(GaussianSplatAsset.TexType.Col));
            long sizeSh = 0;
            for (var i = GaussianSplatAsset.TexType.SH1; i <= GaussianSplatAsset.TexType.SHF; ++i)
            {
                sizeSh += GetTextureSize(gs.GetTex(i));
            }
            EditorGUILayout.TextField("Memory", EditorUtility.FormatBytes(sizePos + sizeRot + sizeScl + sizeCol + sizeSh));
            EditorGUI.indentLevel++;
            EditorGUILayout.TextField("Positions", EditorUtility.FormatBytes(sizePos));
            EditorGUILayout.TextField("Rotations", EditorUtility.FormatBytes(sizeRot));
            EditorGUILayout.TextField("Scales", EditorUtility.FormatBytes(sizeScl));
            EditorGUILayout.TextField("Base color", EditorUtility.FormatBytes(sizeCol));
            EditorGUILayout.TextField("SHs", EditorUtility.FormatBytes(sizeSh));
            EditorGUI.indentLevel--;

            EditorGUILayout.Vector3Field("Bounds Min", gs.m_BoundsMin);
            EditorGUILayout.Vector3Field("Bounds Max", gs.m_BoundsMax);
        }

        /*
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Calc Stats"))
            CalcStats(gs.pointCloudFolder, gs.m_Use30kVersion);
        if (GUILayout.Button("Clear Stats", GUILayout.ExpandWidth(false)))
            ClearStats();
        GUILayout.EndHorizontal();

        if (m_StatsTexture && m_CachedDataRanges != null)
        {
            var distRect = GUILayoutUtility.GetRect(100, kFieldNames.Length * kRowHeight);
            var graphRect = new Rect(distRect.x + 70, distRect.y, distRect.width - 110, distRect.height);
            GUI.Box(graphRect, GUIContent.none);
            for (int bi = 0; bi < kFieldNames.Length; ++bi)
            {
                var rowRect = new Rect(distRect.x, distRect.y + bi * kRowHeight, distRect.width, kRowHeight);
                GUI.Label(new Rect(rowRect.x, rowRect.y, 30, rowRect.height), kFieldNames[bi], EditorStyles.miniLabel);
                GUI.Label(new Rect(rowRect.x + 30, rowRect.y, 40, rowRect.height),
                    m_CachedDataRanges[bi].x.ToString("F3"), EditorStyles.miniLabel);
                GUI.Label(new Rect(rowRect.xMax - 40, rowRect.y, 40, rowRect.height),
                    m_CachedDataRanges[bi].y.ToString("F3"), EditorStyles.miniLabel);
            }
            GUI.DrawTexture(graphRect, m_StatsTexture, ScaleMode.StretchToFill);
        }
        */
    }

    /*
    struct Color64
    {
        public ushort r, g, b, a;
    }

    [BurstCompile]
    struct CalcStatsJob : IJobParallelFor
    {
        public int pixelsWidth;
        public int pixelsHeight;
        [NativeDisableParallelForRestriction] public NativeArray<Color64> pixels;
        public NativeArray<Vector2> ranges;
        [ReadOnly] public NativeArray<float> data;
        public int itemCount;
        public int itemStrideInFloats;

        static float SquareCentered01(float x)
        {
            x -= 0.5f;
            x *= x * math.sign(x);
            return x * 2.0f + 0.5f;
        }

        static float InvSquareCentered01(float x)
        {
            x -= 0.5f;
            x *= 0.5f;
            x = math.sqrt(math.abs(x)) * math.sign(x);
            return x + 0.5f;
        }

        static float AdjustVal(float val, int fieldIndex)
        {
            if (fieldIndex >= 55 && fieldIndex < 58) // scale: exp
            {
                val = math.exp(val);
                // make them distributed more equally
                val = math.pow(val, 1.0f / 8.0f);
            }

            if (fieldIndex == 54) // opacity: sigmoid
            {
                val = 1.0f / (1.0f + math.exp(-val));
                // make them distributed more equally
                val = SquareCentered01(val);
            }

            return val;
        }

        public void Execute(int fieldIndex)
        {
            // find min/max
            Vector2 range = new Vector2(float.PositiveInfinity, float.NegativeInfinity);
            int idx = fieldIndex;
            for (int si = 0; si < itemCount; ++si)
            {
                float val = AdjustVal(data[idx], fieldIndex);
                range.x = math.min(range.x, val);
                range.y = math.max(range.y, val);
                idx += itemStrideInFloats;
            }
            ranges[fieldIndex] = range;

            // fill texture with value distribution over the range
            idx = fieldIndex;
            for (int si = 0; si < itemCount; ++si)
            {
                float val = AdjustVal(data[idx], fieldIndex);
                val = math.unlerp(range.x, range.y, val);
                val = math.saturate(val);
                int px = (int) math.floor(val * (pixelsWidth-1));
                int py = pixelsHeight - 2 - (fieldIndex * kRowHeight + 1 + (si % (kRowHeight - 4)));
                int pidx = py * pixelsWidth + px;

                Color64 col = pixels[pidx];
                col.r = (ushort)math.min(0xFFFF, col.r + 23);
                col.g = (ushort)math.min(0xFFFF, col.g + 7);
                col.b = (ushort)math.min(0xFFFF, col.b + 1);
                col.a = 0xFFFF;
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
    void CalcStats(string pointCloudFolder, bool use30k)
    {
        ClearStats();
        NativeArray<GaussianSplatRenderer.InputSplat> splats = GaussianSplatRenderer.LoadPLYSplatFile(pointCloudFolder, use30k);
        if (!splats.IsCreated)
            return;

        int itemSizeBytes = UnsafeUtility.SizeOf<GaussianSplatRenderer.InputSplat>();
        int fieldCount = itemSizeBytes / 4;

        if (!m_StatsTexture)
            m_StatsTexture = new Texture2D(512, fieldCount * kRowHeight, GraphicsFormat.R16G16B16A16_UNorm, TextureCreationFlags.None);
        NativeArray<Color64> statsPixels = new(m_StatsTexture.width * m_StatsTexture.height, Allocator.TempJob);
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
    */
}
