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

            long sizePos = GetTextureSize(gs.m_TexPos);
            long sizeRot = GetTextureSize(gs.m_TexRot);
            long sizeScl = GetTextureSize(gs.m_TexScl);
            long sizeCol = GetTextureSize(gs.m_TexCol);
            long sizeSh = 0;
            sizeSh += GetTextureSize(gs.m_TexSH1);
            sizeSh += GetTextureSize(gs.m_TexSH2);
            sizeSh += GetTextureSize(gs.m_TexSH3);
            sizeSh += GetTextureSize(gs.m_TexSH4);
            sizeSh += GetTextureSize(gs.m_TexSH5);
            sizeSh += GetTextureSize(gs.m_TexSH6);
            sizeSh += GetTextureSize(gs.m_TexSH7);
            sizeSh += GetTextureSize(gs.m_TexSH8);
            sizeSh += GetTextureSize(gs.m_TexSH9);
            sizeSh += GetTextureSize(gs.m_TexSHA);
            sizeSh += GetTextureSize(gs.m_TexSHB);
            sizeSh += GetTextureSize(gs.m_TexSHC);
            sizeSh += GetTextureSize(gs.m_TexSHD);
            sizeSh += GetTextureSize(gs.m_TexSHE);
            sizeSh += GetTextureSize(gs.m_TexSHF);
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

    // Based on https://fgiesen.wordpress.com/2009/12/13/decoding-morton-codes/
    //
    // "Insert" two 0 bits after each of the 21 low bits of x
    static ulong MortonPart1By2(ulong x)
    {
        x &= 0x1fffff;
        x = (x ^ (x << 32)) & 0x1f00000000ffffUL;
        x = (x ^ (x << 16)) & 0x1f0000ff0000ffUL;
        x = (x ^ (x << 8)) & 0x100f00f00f00f00fUL;
        x = (x ^ (x << 4)) & 0x10c30c30c30c30c3UL;
        x = (x ^ (x << 2)) & 0x1249249249249249UL;
        return x;
    }
    // Encode three 21-bit integers into 3D Morton order
    static ulong MortonEncode3(uint3 v)
    {
        return (MortonPart1By2(v.z) << 2) | (MortonPart1By2(v.y) << 1) | MortonPart1By2(v.x);
    }


    [BurstCompile]
    struct ReorderMortonJob : IJob
    {
        public NativeArray<GaussianSplatAssetCreator.InputSplatData> m_SplatData;
        public NativeArray<(ulong,int)> m_Order;

        public void Execute()
        {
            float3 boundsMin = float.PositiveInfinity;
            float3 boundsMax = float.NegativeInfinity;

            for (int i = 0; i < m_SplatData.Length; ++i)
            {
                float3 pos = m_SplatData[i].pos;
                boundsMin = math.min(boundsMin, pos);
                boundsMax = math.max(boundsMax, pos);
            }

            float kScaler = (float) ((1 << 21) - 1);
            float3 invBoundsSize = new float3(1.0f) / (boundsMax - boundsMin);
            for (int i = 0; i < m_SplatData.Length; ++i)
            {
                float3 pos = ((float3)m_SplatData[i].pos - boundsMin) * invBoundsSize * kScaler;
                uint3 ipos = (uint3) pos;
                ulong code = MortonEncode3(ipos);
                m_Order[i] = (code, i);
            }
        }
    }

    struct OrderComparer : IComparer<(ulong, int)> {
        public int Compare((ulong, int) a, (ulong, int) b)
        {
            if (a.Item1 < b.Item1)
                return -1;
            if (a.Item1 > b.Item1)
                return +1;
            return a.Item2 - b.Item2;
        }
    }

    static void ReorderMorton(GaussianSplatRenderer gs)
    {
        float t0 = Time.realtimeSinceStartup;
        ReorderMortonJob order = new ReorderMortonJob
        {
            m_SplatData = gs.splatData,
            m_Order = new NativeArray<(ulong, int)>(gs.splatData.Length, Allocator.TempJob)
        };
        order.Schedule().Complete();
        order.m_Order.Sort(new OrderComparer());

        NativeArray<GaussianSplatRenderer.InputSplat> copy = new(order.m_SplatData, Allocator.TempJob);
        for (int i = 0; i < copy.Length; ++i)
            order.m_SplatData[i] = copy[order.m_Order[i].Item2];
        copy.Dispose();

        order.m_Order.Dispose();
        gs.UpdateGPUBuffers();
        float t1 = Time.realtimeSinceStartup;
        Debug.Log($"Reordered {gs.splatData.Length:N0} splats to Morton order in {(t1-t0):F1}sec");
    }
        */
}
