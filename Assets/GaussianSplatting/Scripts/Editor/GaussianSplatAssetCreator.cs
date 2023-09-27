using System;
using System.Collections.Generic;
using System.IO;
using TinyJson;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[BurstCompile]
public class GaussianSplatAssetCreator : EditorWindow
{
    const string kProgressTitle = "Creating Gaussian Splat Asset";
    const string kPointCloudPly = "point_cloud/iteration_7000/point_cloud.ply";
    const string kPointCloud30kPly = "point_cloud/iteration_30000/point_cloud.ply";
    const string kCamerasJson = "cameras.json";

    enum DataQuality
    {
        VeryHigh,
        High,
        Medium,
        Low,
        VeryLow,
        Custom,
    }

    enum ColorFormat
    {
        Float16x4,
        Norm8x4,
        BC7,
    }

    readonly FolderPickerPropertyDrawer m_FolderPicker = new();

    [SerializeField] string m_InputFolder;
    [SerializeField] bool m_Use30k = true;

    [SerializeField] string m_OutputFolder = "Assets/GaussianAssets";
    [SerializeField] DataQuality m_Quality = DataQuality.Medium;
    [SerializeField] GaussianSplatAsset.VectorFormat m_FormatPos;
    [SerializeField] GaussianSplatAsset.VectorFormat m_FormatScale;
    [SerializeField] GaussianSplatAsset.SHFormat m_FormatSH;
    [SerializeField] ColorFormat m_FormatColor;

    string m_ErrorMessage;
    string m_PrevPlyPath;
    int m_PrevVertexCount;
    long m_PrevFileSize;

    [MenuItem("Tools/Gaussian Splats/Create GaussianSplatAsset")]
    public static void Init()
    {
        var window = GetWindowWithRect<GaussianSplatAssetCreator>(new Rect(50, 50, 360, 340), false, "Gaussian Splat Creator", true);
        window.minSize = new Vector2(320, 320);
        window.maxSize = new Vector2(1500, 1500);
        window.Show();
    }

    void OnEnable()
    {
        ApplyQualityLevel();
    }

    void OnGUI()
    {
        EditorGUILayout.Space();
        GUILayout.Label("Input data", EditorStyles.boldLabel);
        var rect = EditorGUILayout.GetControlRect(true);
        m_InputFolder = m_FolderPicker.PathFieldGUI(rect, new GUIContent("Input Folder"), m_InputFolder, kPointCloudPly, "PointCloudFolder");
        m_Use30k = EditorGUILayout.Toggle(new GUIContent("Use 30k Version", "Use iteration_30000 point cloud if available. Otherwise uses iteration_7000."), m_Use30k);

        string plyPath = GetPLYFileName(m_InputFolder, m_Use30k);
        if (plyPath != m_PrevPlyPath && !string.IsNullOrWhiteSpace(plyPath))
        {
            PLYFileReader.ReadFileHeader(plyPath, out m_PrevVertexCount, out var _, out var _);
            m_PrevFileSize = new FileInfo(plyPath).Length;
            m_PrevPlyPath = plyPath;
        }

        if (m_PrevVertexCount > 0)
            EditorGUILayout.LabelField("File Size", $"{EditorUtility.FormatBytes(m_PrevFileSize)} - {m_PrevVertexCount:N0} splats");
        else
            GUILayout.Space(EditorGUIUtility.singleLineHeight);

        EditorGUILayout.Space();
        GUILayout.Label("Output", EditorStyles.boldLabel);
        rect = EditorGUILayout.GetControlRect(true);
        m_OutputFolder = m_FolderPicker.PathFieldGUI(rect, new GUIContent("Output Folder"), m_OutputFolder, null, "GaussianAssetOutputFolder");
        var newQuality = (DataQuality) EditorGUILayout.EnumPopup("Quality", m_Quality);
        if (newQuality != m_Quality)
        {
            m_Quality = newQuality;
            ApplyQualityLevel();
        }

        long sizePos = 0, sizeOther = 0, sizeCol = 0, sizeSHs = 0, totalSize = 0;
        if (m_PrevVertexCount > 0)
        {
            sizePos = GaussianSplatAsset.CalcPosDataSize(m_PrevVertexCount, m_FormatPos);
            sizeOther = GaussianSplatAsset.CalcOtherDataSize(m_PrevVertexCount, m_FormatScale);
            sizeCol = GaussianSplatAsset.CalcColorDataSize(m_PrevVertexCount, ColorFormatToGraphics(m_FormatColor));
            sizeSHs = GaussianSplatAsset.CalcSHDataSize(m_PrevVertexCount, m_FormatSH);
            long sizeChunk = GaussianSplatAsset.CalcChunkDataSize(m_PrevVertexCount);
            totalSize = sizePos + sizeOther + sizeCol + sizeSHs + sizeChunk;
        }

        const float kSizeColWidth = 70;
        EditorGUI.BeginDisabledGroup(m_Quality != DataQuality.Custom);
        EditorGUI.indentLevel++;
        GUILayout.BeginHorizontal();
        m_FormatPos = (GaussianSplatAsset.VectorFormat)EditorGUILayout.EnumPopup("Position", m_FormatPos);
        GUILayout.Label(sizePos > 0 ? EditorUtility.FormatBytes(sizePos) : string.Empty, GUILayout.Width(kSizeColWidth));
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        m_FormatScale = (GaussianSplatAsset.VectorFormat)EditorGUILayout.EnumPopup("Scale", m_FormatScale);
        GUILayout.Label(sizeOther > 0 ? EditorUtility.FormatBytes(sizeOther) : string.Empty, GUILayout.Width(kSizeColWidth));
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        m_FormatColor = (ColorFormat)EditorGUILayout.EnumPopup("Color", m_FormatColor);
        GUILayout.Label(sizeCol > 0 ? EditorUtility.FormatBytes(sizeCol) : string.Empty, GUILayout.Width(kSizeColWidth));
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        m_FormatSH = (GaussianSplatAsset.SHFormat) EditorGUILayout.EnumPopup("SH", m_FormatSH);
        GUIContent shGC = new GUIContent();
        shGC.text = sizeSHs > 0 ? EditorUtility.FormatBytes(sizeSHs) : string.Empty;
        if (m_FormatSH >= GaussianSplatAsset.SHFormat.Cluster64k)
        {
            shGC.tooltip = "Note that SH clustering is not fast! (3-10 minutes for 6M splats)";
            shGC.image = EditorGUIUtility.IconContent("console.warnicon.sml").image;
        }
        GUILayout.Label(shGC, GUILayout.Width(kSizeColWidth));
        GUILayout.EndHorizontal();
        EditorGUI.indentLevel--;
        EditorGUI.EndDisabledGroup();
        if (totalSize > 0)
            EditorGUILayout.LabelField("Asset Size", $"{EditorUtility.FormatBytes(totalSize)} - {(double) m_PrevFileSize / totalSize:F1}x smaller");
        else
            GUILayout.Space(EditorGUIUtility.singleLineHeight);


        EditorGUILayout.Space();
        GUILayout.BeginHorizontal();
        GUILayout.Space(30);
        if (GUILayout.Button("Create Asset"))
        {
            CreateAsset();
        }
        GUILayout.Space(30);
        GUILayout.EndHorizontal();

        if (!string.IsNullOrWhiteSpace(m_ErrorMessage))
        {
            EditorGUILayout.HelpBox(m_ErrorMessage, MessageType.Error);
        }
    }

    void ApplyQualityLevel()
    {
        switch (m_Quality)
        {
            case DataQuality.Custom:
                break;
            case DataQuality.VeryLow: // 18.4x smaller, 32.27 PSNR (was: 20.7x smaller, 24.07 PSNR)
                m_FormatPos = GaussianSplatAsset.VectorFormat.Norm11;
                m_FormatScale = GaussianSplatAsset.VectorFormat.Norm6;
                m_FormatColor = ColorFormat.BC7;
                m_FormatSH = GaussianSplatAsset.SHFormat.Cluster4k;
                break;
            case DataQuality.Low: // 14.9x smaller, 35.17 PSNR (was: 13.1x smaller, 34.76 PSNR)
                m_FormatPos = GaussianSplatAsset.VectorFormat.Norm11;
                m_FormatScale = GaussianSplatAsset.VectorFormat.Norm6;
                m_FormatColor = ColorFormat.Norm8x4;
                m_FormatSH = GaussianSplatAsset.SHFormat.Cluster16k;
                break;
            case DataQuality.Medium: // 5.1x smaller, 47.46 PSNR (was: 5.3x smaller, 47.51 PSNR)
                m_FormatPos = GaussianSplatAsset.VectorFormat.Norm11;
                m_FormatScale = GaussianSplatAsset.VectorFormat.Norm11;
                m_FormatColor = ColorFormat.Norm8x4;
                m_FormatSH = GaussianSplatAsset.SHFormat.Norm6;
                break;
            case DataQuality.High: // 2.9x smaller, 57.77 PSNR (was: 2.9x smaller, 54.87 PSNR)
                m_FormatPos = GaussianSplatAsset.VectorFormat.Norm16;
                m_FormatScale = GaussianSplatAsset.VectorFormat.Norm16;
                m_FormatColor = ColorFormat.Float16x4;
                m_FormatSH = GaussianSplatAsset.SHFormat.Norm11;
                break;
            case DataQuality.VeryHigh: // 2.1x smaller (was: 0.8x smaller)
                m_FormatPos = GaussianSplatAsset.VectorFormat.Norm16;
                m_FormatScale = GaussianSplatAsset.VectorFormat.Norm16;
                m_FormatColor = ColorFormat.Float16x4;
                m_FormatSH = GaussianSplatAsset.SHFormat.Float16;
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    // input file splat data is expected to be in this format
    public struct InputSplatData
    {
        public Vector3 pos;
        public Vector3 nor;
        public Vector3 dc0;
        public Vector3 sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, shA, shB, shC, shD, shE, shF;
        public float opacity;
        public Vector3 scale;
        public Quaternion rot;
    }

    static T CreateOrReplaceAsset<T>(T asset, string path) where T : UnityEngine.Object
    {
        T result = AssetDatabase.LoadAssetAtPath<T>(path);
        if (result == null)
        {
            AssetDatabase.CreateAsset(asset, path);
            result = asset;
        }
        else
        {
            if (typeof(Mesh).IsAssignableFrom(typeof(T))) { (result as Mesh)?.Clear(); }
            EditorUtility.CopySerialized(asset, result);
        }
        return result;
    }

    unsafe void CreateAsset()
    {
        m_ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(m_InputFolder))
        {
            m_ErrorMessage = $"Select input folder";
            return;
        }

        if (string.IsNullOrWhiteSpace(m_OutputFolder) || !m_OutputFolder.StartsWith("Assets/"))
        {
            m_ErrorMessage = $"Output folder must be within project, was '{m_OutputFolder}'";
            return;
        }
        Directory.CreateDirectory(m_OutputFolder);

        EditorUtility.DisplayProgressBar(kProgressTitle, "Reading data files", 0.0f);
        GaussianSplatAsset.CameraInfo[] cameras = LoadJsonCamerasFile(m_InputFolder);
        using NativeArray<InputSplatData> inputSplats = LoadPLYSplatFile(m_InputFolder, m_Use30k);
        if (inputSplats.Length == 0)
        {
            EditorUtility.ClearProgressBar();
            return;
        }

        float3 boundsMin, boundsMax;
        var boundsJob = new CalcBoundsJob
        {
            m_BoundsMin = &boundsMin,
            m_BoundsMax = &boundsMax,
            m_SplatData = inputSplats
        };
        boundsJob.Schedule().Complete();

        EditorUtility.DisplayProgressBar(kProgressTitle, "Morton reordering", 0.05f);
        ReorderMorton(inputSplats, boundsMin, boundsMax);

        // cluster SHs
        NativeArray<int> splatSHIndices = default;
        NativeArray<GaussianSplatAsset.SHTableItemFloat16> clusteredSHs = default;
        if (m_FormatSH >= GaussianSplatAsset.SHFormat.Cluster64k)
        {
            EditorUtility.DisplayProgressBar(kProgressTitle, "Cluster SHs", 0.2f);
            ClusterSHs(inputSplats, m_FormatSH, out clusteredSHs, out splatSHIndices);
        }

        string baseName = Path.GetFileNameWithoutExtension(m_InputFolder) + (m_Use30k ? "_30k" : "_7k");

        GaussianSplatAsset asset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
        asset.name = baseName;
        asset.m_Cameras = cameras;
        asset.m_BoundsMin = boundsMin;
        asset.m_BoundsMax = boundsMax;

        EditorUtility.DisplayProgressBar(kProgressTitle, "Calc chunks", 0.7f);
        LinearizeData(inputSplats);
        asset.m_Chunks = CalcChunkData(inputSplats);

        EditorUtility.DisplayProgressBar(kProgressTitle, "Creating data objects", 0.75f);
        asset.m_SplatCount = inputSplats.Length;
        asset.m_FormatVersion = GaussianSplatAsset.kCurrentVersion;
        asset.m_PosFormat = m_FormatPos;
        asset.m_ScaleFormat = m_FormatScale;
        asset.m_SHFormat = m_FormatSH;
        asset.m_DataHash = new Hash128((uint)asset.m_SplatCount, (uint)asset.m_FormatVersion, 0, 0);
        string pathPos = $"{m_OutputFolder}/{baseName}_pos.bytes";
        string pathOther = $"{m_OutputFolder}/{baseName}_oth.bytes";
        string pathCol = $"{m_OutputFolder}/{baseName}_col.gstex";
        string pathSh = $"{m_OutputFolder}/{baseName}_shs.bytes";
        CreatePositionsData(inputSplats, pathPos, ref asset.m_DataHash);
        CreateOtherData(inputSplats, pathOther, ref asset.m_DataHash, splatSHIndices);
        CreateColorData(inputSplats, pathCol, ref asset.m_DataHash);
        CreateSHData(inputSplats, pathSh, ref asset.m_DataHash, clusteredSHs);

        splatSHIndices.Dispose();
        clusteredSHs.Dispose();

        // files are created, import them so we can get to the imported objects, ugh
        EditorUtility.DisplayProgressBar(kProgressTitle, "Initial texture import", 0.85f);
        AssetDatabase.Refresh(ImportAssetOptions.ForceUncompressedImport);

        EditorUtility.DisplayProgressBar(kProgressTitle, "Setup data onto asset", 0.95f);
        asset.m_PosData = AssetDatabase.LoadAssetAtPath<TextAsset>(pathPos);
        asset.m_OtherData = AssetDatabase.LoadAssetAtPath<TextAsset>(pathOther);
        asset.m_ColorData = AssetDatabase.LoadAssetAtPath<Texture2D>(pathCol);
        asset.m_SHData = AssetDatabase.LoadAssetAtPath<TextAsset>(pathSh);

        var assetPath = $"{m_OutputFolder}/{baseName}.asset";
        var savedAsset = CreateOrReplaceAsset(asset, assetPath);

        EditorUtility.DisplayProgressBar(kProgressTitle, "Saving assets", 0.99f);
        AssetDatabase.SaveAssets();
        EditorUtility.ClearProgressBar();

        Selection.activeObject = savedAsset;
    }

    static string GetPLYFileName(string folder, bool use30k)
    {
        string plyPath = $"{folder}/{(use30k ? kPointCloud30kPly : kPointCloudPly)}";
        if (!File.Exists(plyPath))
        {
            plyPath = $"{folder}/{kPointCloudPly}";
            if (!File.Exists(plyPath))
            {
                return null;
            }
        }
        return plyPath;
    }

    unsafe NativeArray<InputSplatData> LoadPLYSplatFile(string folder, bool use30k)
    {
        NativeArray<InputSplatData> data = default;
        string plyPath = GetPLYFileName(folder, use30k);
        if (string.IsNullOrWhiteSpace(plyPath))
        {
            m_ErrorMessage = $"Did not find {plyPath} file";
            return data;
        }

        PLYFileReader.ReadFile(plyPath, out var splatCount, out int vertexStride, out _, out var verticesRawData);
        if (UnsafeUtility.SizeOf<InputSplatData>() != vertexStride)
        {
            m_ErrorMessage = $"InputVertex size mismatch, we expect {UnsafeUtility.SizeOf<InputSplatData>()} but file has {vertexStride}";
            return data;
        }

        // reorder SHs
        NativeArray<float> floatData = verticesRawData.Reinterpret<float>(1);
        ReorderSHs(splatCount, (float*)floatData.GetUnsafePtr());

        return verticesRawData.Reinterpret<InputSplatData>(1);
    }

    [BurstCompile]
    static unsafe void ReorderSHs(int splatCount, float* data)
    {
        int splatStride = UnsafeUtility.SizeOf<InputSplatData>() / 4;
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

    [BurstCompile]
    struct CalcBoundsJob : IJob
    {
        [NativeDisableUnsafePtrRestriction] public unsafe float3* m_BoundsMin;
        [NativeDisableUnsafePtrRestriction] public unsafe float3* m_BoundsMax;
        [ReadOnly] public NativeArray<InputSplatData> m_SplatData;

        public unsafe void Execute()
        {
            float3 boundsMin = float.PositiveInfinity;
            float3 boundsMax = float.NegativeInfinity;

            for (int i = 0; i < m_SplatData.Length; ++i)
            {
                float3 pos = m_SplatData[i].pos;
                boundsMin = math.min(boundsMin, pos);
                boundsMax = math.max(boundsMax, pos);
            }
            *m_BoundsMin = boundsMin;
            *m_BoundsMax = boundsMax;
        }
    }


    [BurstCompile]
    struct ReorderMortonJob : IJobParallelFor
    {
        const float kScaler = (float) ((1 << 21) - 1);
        public float3 m_BoundsMin;
        public float3 m_InvBoundsSize;
        [ReadOnly] public NativeArray<InputSplatData> m_SplatData;
        public NativeArray<(ulong,int)> m_Order;

        public void Execute(int index)
        {
            float3 pos = ((float3)m_SplatData[index].pos - m_BoundsMin) * m_InvBoundsSize * kScaler;
            uint3 ipos = (uint3) pos;
            ulong code = GaussianUtils.MortonEncode3(ipos);
            m_Order[index] = (code, index);
        }
    }

    struct OrderComparer : IComparer<(ulong, int)> {
        public int Compare((ulong, int) a, (ulong, int) b)
        {
            if (a.Item1 < b.Item1) return -1;
            if (a.Item1 > b.Item1) return +1;
            return a.Item2 - b.Item2;
        }
    }

    static void ReorderMorton(NativeArray<InputSplatData> splatData, float3 boundsMin, float3 boundsMax)
    {
        ReorderMortonJob order = new ReorderMortonJob
        {
            m_SplatData = splatData,
            m_BoundsMin = boundsMin,
            m_InvBoundsSize = 1.0f / (boundsMax - boundsMin),
            m_Order = new NativeArray<(ulong, int)>(splatData.Length, Allocator.TempJob)
        };
        order.Schedule(splatData.Length, 4096).Complete();
        order.m_Order.Sort(new OrderComparer());

        NativeArray<InputSplatData> copy = new(order.m_SplatData, Allocator.TempJob);
        for (int i = 0; i < copy.Length; ++i)
            order.m_SplatData[i] = copy[order.m_Order[i].Item2];
        copy.Dispose();

        order.m_Order.Dispose();
    }

    [BurstCompile]
    static unsafe void GatherSHs(int splatCount, InputSplatData* splatData, float* shData)
    {
        for (int i = 0; i < splatCount; ++i)
        {
            UnsafeUtility.MemCpy(shData, ((float*)splatData) + 9, 15 * 3 * sizeof(float));
            splatData++;
            shData += 15 * 3;
        }
    }

    [BurstCompile]
    struct ConvertSHClustersJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> m_Input;
        public NativeArray<GaussianSplatAsset.SHTableItemFloat16> m_Output;
        public void Execute(int index)
        {
            var addr = index * 15;
            GaussianSplatAsset.SHTableItemFloat16 res;
            res.sh1 = new half3(m_Input[addr+0]);
            res.sh2 = new half3(m_Input[addr+1]);
            res.sh3 = new half3(m_Input[addr+2]);
            res.sh4 = new half3(m_Input[addr+3]);
            res.sh5 = new half3(m_Input[addr+4]);
            res.sh6 = new half3(m_Input[addr+5]);
            res.sh7 = new half3(m_Input[addr+6]);
            res.sh8 = new half3(m_Input[addr+7]);
            res.sh9 = new half3(m_Input[addr+8]);
            res.shA = new half3(m_Input[addr+9]);
            res.shB = new half3(m_Input[addr+10]);
            res.shC = new half3(m_Input[addr+11]);
            res.shD = new half3(m_Input[addr+12]);
            res.shE = new half3(m_Input[addr+13]);
            res.shF = new half3(m_Input[addr+14]);
            res.shPadding = default;
            m_Output[index] = res;
        }
    }
    static bool ClusterSHProgress(float val)
    {
        EditorUtility.DisplayProgressBar(kProgressTitle, $"Cluster SHs ({val:P0})", 0.2f + val * 0.5f);
        return true;
    }

    static unsafe void ClusterSHs(NativeArray<InputSplatData> splatData, GaussianSplatAsset.SHFormat format, out NativeArray<GaussianSplatAsset.SHTableItemFloat16> shs, out NativeArray<int> shIndices)
    {
        shs = default;
        shIndices = default;

        int shCount = GaussianSplatAsset.GetSHCount(format, splatData.Length);
        if (shCount >= splatData.Length) // no need to cluster, just use raw data
            return;

        const int kShDim = 15 * 3;
        const int kBatchSize = 2048;
        float passesOverData = format switch
        {
            GaussianSplatAsset.SHFormat.Cluster64k => 0.3f,
            GaussianSplatAsset.SHFormat.Cluster32k => 0.4f,
            GaussianSplatAsset.SHFormat.Cluster16k => 0.5f,
            GaussianSplatAsset.SHFormat.Cluster8k => 0.8f,
            GaussianSplatAsset.SHFormat.Cluster4k => 1.2f,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
        };

        float t0 = Time.realtimeSinceStartup;
        NativeArray<float> shData = new(splatData.Length * kShDim, Allocator.Persistent);
        GatherSHs(splatData.Length, (InputSplatData*) splatData.GetUnsafeReadOnlyPtr(), (float*) shData.GetUnsafePtr());

        NativeArray<float> shMeans = new(shCount * kShDim, Allocator.Persistent);
        shIndices = new(splatData.Length, Allocator.Persistent);

        KMeansClustering.Calculate(kShDim, shData, kBatchSize, passesOverData, ClusterSHProgress, shMeans, shIndices);
        shData.Dispose();

        shs = new NativeArray<GaussianSplatAsset.SHTableItemFloat16>(shCount, Allocator.Persistent);

        ConvertSHClustersJob job = new ConvertSHClustersJob
        {
            m_Input = shMeans.Reinterpret<float3>(4),
            m_Output = shs
        };
        job.Schedule(shCount, 256).Complete();
        shMeans.Dispose();
        float t1 = Time.realtimeSinceStartup;
        Debug.Log($"GS: clustered {splatData.Length/1000000.0:F2}M SHs into {shCount/1024}K ({passesOverData:F1}pass/{kBatchSize}batch) in {t1-t0:F0}s");
    }

    [BurstCompile]
    struct LinearizeDataJob : IJobParallelFor
    {
        public NativeArray<InputSplatData> splatData;
        public void Execute(int index)
        {
            var splat = splatData[index];

            // rot
            var q = splat.rot;
            var qq = GaussianUtils.NormalizeSwizzleRotation(new float4(q.x, q.y, q.z, q.w));
            qq = GaussianUtils.PackSmallest3Rotation(qq);
            splat.rot = new Quaternion(qq.x, qq.y, qq.z, qq.w);

            // scale
            splat.scale = GaussianUtils.LinearScale(splat.scale);
            // transform scale to be more uniformly distributed
            splat.scale = math.pow(splat.scale, 1.0f / 8.0f);

            // color
            splat.dc0 = GaussianUtils.SH0ToColor(splat.dc0);
            splat.opacity = GaussianUtils.SquareCentered01(GaussianUtils.Sigmoid(splat.opacity));

            splatData[index] = splat;
        }
    }

    static void LinearizeData(NativeArray<InputSplatData> splatData)
    {
        LinearizeDataJob job = new LinearizeDataJob();
        job.splatData = splatData;
        job.Schedule(splatData.Length, 4096).Complete();
    }

    [BurstCompile]
    struct CalcChunkDataJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] public NativeArray<InputSplatData> splatData;
        public NativeArray<GaussianSplatAsset.ChunkInfo> chunks;

        public void Execute(int chunkIdx)
        {
            GaussianSplatAsset.BoundsInfo chunkMin;
            chunkMin.pos = (float3) float.PositiveInfinity;
            chunkMin.scl = (float3) float.PositiveInfinity;
            chunkMin.col = (float4) float.PositiveInfinity;
            chunkMin.shs = (float3) float.PositiveInfinity;
            GaussianSplatAsset.BoundsInfo chunkMax;
            chunkMax.pos = (float3) float.NegativeInfinity;
            chunkMax.scl = (float3) float.NegativeInfinity;
            chunkMax.col = (float4) float.NegativeInfinity;
            chunkMax.shs = (float3) float.NegativeInfinity;

            int splatBegin = math.min(chunkIdx * GaussianSplatAsset.kChunkSize, splatData.Length);
            int splatEnd = math.min((chunkIdx + 1) * GaussianSplatAsset.kChunkSize, splatData.Length);

            // calculate data bounds inside the chunk
            for (int i = splatBegin; i < splatEnd; ++i)
            {
                InputSplatData s = splatData[i];
                chunkMin.pos = math.min(chunkMin.pos, s.pos);
                chunkMin.scl = math.min(chunkMin.scl, s.scale);
                chunkMin.col = math.min(chunkMin.col, new float4(s.dc0, s.opacity));
                chunkMin.shs = math.min(chunkMin.shs, s.sh1);
                chunkMin.shs = math.min(chunkMin.shs, s.sh2);
                chunkMin.shs = math.min(chunkMin.shs, s.sh3);
                chunkMin.shs = math.min(chunkMin.shs, s.sh4);
                chunkMin.shs = math.min(chunkMin.shs, s.sh5);
                chunkMin.shs = math.min(chunkMin.shs, s.sh6);
                chunkMin.shs = math.min(chunkMin.shs, s.sh7);
                chunkMin.shs = math.min(chunkMin.shs, s.sh8);
                chunkMin.shs = math.min(chunkMin.shs, s.sh9);
                chunkMin.shs = math.min(chunkMin.shs, s.shA);
                chunkMin.shs = math.min(chunkMin.shs, s.shB);
                chunkMin.shs = math.min(chunkMin.shs, s.shC);
                chunkMin.shs = math.min(chunkMin.shs, s.shD);
                chunkMin.shs = math.min(chunkMin.shs, s.shE);
                chunkMin.shs = math.min(chunkMin.shs, s.shF);

                chunkMax.pos = math.max(chunkMax.pos, s.pos);
                chunkMax.scl = math.max(chunkMax.scl, s.scale);
                chunkMax.col = math.max(chunkMax.col, new float4(s.dc0, s.opacity));
                chunkMax.shs = math.max(chunkMax.shs, s.sh1);
                chunkMax.shs = math.max(chunkMax.shs, s.sh2);
                chunkMax.shs = math.max(chunkMax.shs, s.sh3);
                chunkMax.shs = math.max(chunkMax.shs, s.sh4);
                chunkMax.shs = math.max(chunkMax.shs, s.sh5);
                chunkMax.shs = math.max(chunkMax.shs, s.sh6);
                chunkMax.shs = math.max(chunkMax.shs, s.sh7);
                chunkMax.shs = math.max(chunkMax.shs, s.sh8);
                chunkMax.shs = math.max(chunkMax.shs, s.sh9);
                chunkMax.shs = math.max(chunkMax.shs, s.shA);
                chunkMax.shs = math.max(chunkMax.shs, s.shB);
                chunkMax.shs = math.max(chunkMax.shs, s.shC);
                chunkMax.shs = math.max(chunkMax.shs, s.shD);
                chunkMax.shs = math.max(chunkMax.shs, s.shE);
                chunkMax.shs = math.max(chunkMax.shs, s.shF);
            }

            // store chunk info
            GaussianSplatAsset.ChunkInfo info;
            info.boundsMin = chunkMin;
            info.boundsMax = chunkMax;
            chunks[chunkIdx] = info;

            // adjust data to be 0..1 within chunk bounds
            for (int i = splatBegin; i < splatEnd; ++i)
            {
                InputSplatData s = splatData[i];
                s.pos = (s.pos - chunkMin.pos) / (float3)(chunkMax.pos - chunkMin.pos);
                s.scale = (s.scale - chunkMin.scl) / (float3)(chunkMax.scl - chunkMin.scl);
                s.dc0 = ((float3)s.dc0 - ((float4)chunkMin.col).xyz) / (((float4)chunkMax.col).xyz - ((float4)chunkMin.col).xyz);
                s.opacity = (s.opacity - chunkMin.col.w) / (chunkMax.col.w - chunkMin.col.w);
                s.sh1 = (s.sh1 - chunkMin.shs) / (float3)(chunkMax.shs - chunkMin.shs);
                s.sh2 = (s.sh2 - chunkMin.shs) / (float3)(chunkMax.shs - chunkMin.shs);
                s.sh3 = (s.sh3 - chunkMin.shs) / (float3)(chunkMax.shs - chunkMin.shs);
                s.sh4 = (s.sh4 - chunkMin.shs) / (float3)(chunkMax.shs - chunkMin.shs);
                s.sh5 = (s.sh5 - chunkMin.shs) / (float3)(chunkMax.shs - chunkMin.shs);
                s.sh6 = (s.sh6 - chunkMin.shs) / (float3)(chunkMax.shs - chunkMin.shs);
                s.sh7 = (s.sh7 - chunkMin.shs) / (float3)(chunkMax.shs - chunkMin.shs);
                s.sh8 = (s.sh8 - chunkMin.shs) / (float3)(chunkMax.shs - chunkMin.shs);
                s.sh9 = (s.sh9 - chunkMin.shs) / (float3)(chunkMax.shs - chunkMin.shs);
                s.shA = (s.shA - chunkMin.shs) / (float3)(chunkMax.shs - chunkMin.shs);
                s.shB = (s.shB - chunkMin.shs) / (float3)(chunkMax.shs - chunkMin.shs);
                s.shC = (s.shC - chunkMin.shs) / (float3)(chunkMax.shs - chunkMin.shs);
                s.shD = (s.shD - chunkMin.shs) / (float3)(chunkMax.shs - chunkMin.shs);
                s.shE = (s.shE - chunkMin.shs) / (float3)(chunkMax.shs - chunkMin.shs);
                s.shF = (s.shF - chunkMin.shs) / (float3)(chunkMax.shs - chunkMin.shs);
                splatData[i] = s;
            }
        }
    }

    static GaussianSplatAsset.ChunkInfo[] CalcChunkData(NativeArray<InputSplatData> splatData)
    {
        int chunkCount = (splatData.Length + GaussianSplatAsset.kChunkSize - 1) / GaussianSplatAsset.kChunkSize;
        CalcChunkDataJob job = new CalcChunkDataJob();
        job.splatData = splatData;
        job.chunks = new(chunkCount, Allocator.TempJob);

        job.Schedule(chunkCount, 8).Complete();

        GaussianSplatAsset.ChunkInfo[] res = job.chunks.ToArray();
        job.chunks.Dispose();
        return res;
    }

    static GraphicsFormat ColorFormatToGraphics(ColorFormat format)
    {
        return format switch
        {
            ColorFormat.Float16x4 => GraphicsFormat.R16G16B16A16_SFloat,
            ColorFormat.Norm8x4 => GraphicsFormat.R8G8B8A8_UNorm,
            ColorFormat.BC7 => GraphicsFormat.RGBA_BC7_UNorm,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
        };
    }

    [BurstCompile]
    struct ConvertColorJob : IJobParallelFor
    {
        public int width, height;
        [ReadOnly] public NativeArray<float4> inputData;
        [NativeDisableParallelForRestriction] public NativeArray<byte> outputData;
        public ColorFormat format;
        public int formatBytesPerPixel;

        public unsafe void Execute(int y)
        {
            int srcIdx = y * width;
            byte* dstPtr = (byte*) outputData.GetUnsafePtr() + y * width * formatBytesPerPixel;
            for (int x = 0; x < width; ++x)
            {
                float4 pix = inputData[srcIdx];

                switch (format)
                {
                    case ColorFormat.Float16x4:
                    {
                        half4 enc = new half4(pix);
                        *(half4*) dstPtr = enc;
                    }
                        break;
                    case ColorFormat.Norm8x4:
                    {
                        pix = math.saturate(pix);
                        uint enc = (uint)(pix.x * 255.5f) | ((uint)(pix.y * 255.5f) << 8) | ((uint)(pix.z * 255.5f) << 16) | ((uint)(pix.w * 255.5f) << 24);
                        *(uint*) dstPtr = enc;
                    }
                        break;
                }

                srcIdx++;
                dstPtr += formatBytesPerPixel;
            }
        }
    }

    static unsafe void SaveTex(string path, int width, int height, NativeArray<float4> data, ColorFormat format)
    {
        GraphicsFormat gfxFormat = ColorFormatToGraphics(format);
        int dstSize = (int)GraphicsFormatUtility.ComputeMipmapSize(width, height, gfxFormat);

        if (GraphicsFormatUtility.IsCompressedFormat(gfxFormat))
        {
            Texture2D tex = new Texture2D(width, height, GraphicsFormat.R32G32B32A32_SFloat, TextureCreationFlags.DontInitializePixels | TextureCreationFlags.DontUploadUponCreate);
            tex.SetPixelData(data, 0);
            EditorUtility.CompressTexture(tex, GraphicsFormatUtility.GetTextureFormat(gfxFormat), 100);
            NativeArray<byte> cmpData = tex.GetPixelData<byte>(0);
            uint2 dataHash = xxHash3.Hash64(cmpData.GetUnsafeReadOnlyPtr(), cmpData.Length);
            GaussianTexImporter.WriteAsset(width, height, gfxFormat, cmpData.AsReadOnlySpan(), (ulong)dataHash.x<<32 | dataHash.y, path);
            DestroyImmediate(tex);
        }
        else
        {
            ConvertColorJob job = new ConvertColorJob
            {
                width = width,
                height = height,
                inputData = data,
                format = format,
                outputData = new NativeArray<byte>(dstSize, Allocator.TempJob),
                formatBytesPerPixel = dstSize / width / height
            };
            job.Schedule(height, 1).Complete();
            uint2 dataHash = xxHash3.Hash64(job.outputData.GetUnsafeReadOnlyPtr(), job.outputData.Length);
            GaussianTexImporter.WriteAsset(width, height, gfxFormat, job.outputData.AsReadOnlySpan(), (ulong)dataHash.x<<32 | dataHash.y, path);
            job.outputData.Dispose();
        }
    }

    static ulong EncodeFloat3ToNorm16(float3 v) // 48 bits: 16.16.16
    {
        return (ulong) (v.x * 65535.5f) | ((ulong) (v.y * 65535.5f) << 16) | ((ulong) (v.z * 65535.5f) << 32);
    }
    static uint EncodeFloat3ToNorm11(float3 v) // 32 bits: 11.10.11
    {
        return (uint) (v.x * 2047.5f) | ((uint) (v.y * 1023.5f) << 11) | ((uint) (v.z * 2047.5f) << 21);
    }
    static ushort EncodeFloat3ToNorm655(float3 v) // 16 bits: 6.5.5
    {
        return (ushort) ((uint) (v.x * 63.5f) | ((uint) (v.y * 31.5f) << 6) | ((uint) (v.z * 31.5f) << 11));
    }
    static ushort EncodeFloat3ToNorm565(float3 v) // 16 bits: 5.6.5
    {
        return (ushort) ((uint) (v.x * 31.5f) | ((uint) (v.y * 63.5f) << 5) | ((uint) (v.z * 31.5f) << 11));
    }

    static uint EncodeQuatToNorm10(float4 v) // 32 bits: 10.10.10.2
    {
        return (uint) (v.x * 1023.5f) | ((uint) (v.y * 1023.5f) << 10) | ((uint) (v.z * 1023.5f) << 20) | ((uint) (v.w * 3.5f) << 30);
    }

    static unsafe void EmitEncodedVector(float3 v, byte* outputPtr, GaussianSplatAsset.VectorFormat format)
    {
        v = math.saturate(v);
        switch (format)
        {
            case GaussianSplatAsset.VectorFormat.Norm16:
                {
                    ulong enc = EncodeFloat3ToNorm16(v);
                    *(uint*) outputPtr = (uint) enc;
                    *(ushort*) (outputPtr + 4) = (ushort) (enc >> 32);
                }
                break;
            case GaussianSplatAsset.VectorFormat.Norm11:
                {
                    uint enc = EncodeFloat3ToNorm11(v);
                    *(uint*) outputPtr = enc;
                }
                break;
            case GaussianSplatAsset.VectorFormat.Norm6:
                {
                    ushort enc = EncodeFloat3ToNorm655(v);
                    *(ushort*) outputPtr = enc;
                }
                break;
        }
    }

    [BurstCompile]
    struct CreatePositionsDataJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<InputSplatData> m_Input;
        public GaussianSplatAsset.VectorFormat m_Format;
        public int m_FormatSize;
        [NativeDisableParallelForRestriction] public NativeArray<byte> m_Output;

        public unsafe void Execute(int index)
        {
            byte* outputPtr = (byte*) m_Output.GetUnsafePtr() + index * m_FormatSize;
            EmitEncodedVector(m_Input[index].pos, outputPtr, m_Format);
        }
    }

    [BurstCompile]
    struct CreateOtherDataJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<InputSplatData> m_Input;
        [NativeDisableContainerSafetyRestriction] [ReadOnly] public NativeArray<int> m_SplatSHIndices;
        public GaussianSplatAsset.VectorFormat m_ScaleFormat;
        public int m_FormatSize;
        [NativeDisableParallelForRestriction] public NativeArray<byte> m_Output;

        public unsafe void Execute(int index)
        {
            byte* outputPtr = (byte*) m_Output.GetUnsafePtr() + index * m_FormatSize;

            // rotation: 4 bytes
            {
                Quaternion rotQ = m_Input[index].rot;
                float4 rot = new float4(rotQ.x, rotQ.y, rotQ.z, rotQ.w);
                uint enc = EncodeQuatToNorm10(rot);
                *(uint*) outputPtr = enc;
                outputPtr += 4;
            }

            // scale: 6, 4 or 2 bytes
            EmitEncodedVector(m_Input[index].scale, outputPtr, m_ScaleFormat);
            outputPtr += GaussianSplatAsset.GetVectorSize(m_ScaleFormat);

            // SH index
            if (m_SplatSHIndices.IsCreated)
                *(ushort*) outputPtr = (ushort)m_SplatSHIndices[index];
        }
    }

    void CreatePositionsData(NativeArray<InputSplatData> inputSplats, string filePath, ref Hash128 dataHash)
    {
        int dataLen = inputSplats.Length * GaussianSplatAsset.GetVectorSize(m_FormatPos);
        dataLen = (dataLen + 3) / 4 * 4; // multiple of 4
        NativeArray<byte> data = new(dataLen, Allocator.TempJob);

        CreatePositionsDataJob job = new CreatePositionsDataJob
        {
            m_Input = inputSplats,
            m_Format = m_FormatPos,
            m_FormatSize = GaussianSplatAsset.GetVectorSize(m_FormatPos),
            m_Output = data
        };
        job.Schedule(inputSplats.Length, 8192).Complete();

        dataHash.Append(data);

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        fs.Write(data);

        data.Dispose();
    }

    void CreateOtherData(NativeArray<InputSplatData> inputSplats, string filePath, ref Hash128 dataHash, NativeArray<int> splatSHIndices)
    {
        int formatSize = GaussianSplatAsset.GetOtherSizeNoSHIndex(m_FormatScale);
        if (splatSHIndices.IsCreated)
            formatSize += 2;
        int dataLen = inputSplats.Length * formatSize;

        dataLen = (dataLen + 3) / 4 * 4; // multiple of 4
        NativeArray<byte> data = new(dataLen, Allocator.TempJob);

        CreateOtherDataJob job = new CreateOtherDataJob
        {
            m_Input = inputSplats,
            m_SplatSHIndices = splatSHIndices,
            m_ScaleFormat = m_FormatScale,
            m_FormatSize = formatSize,
            m_Output = data
        };
        job.Schedule(inputSplats.Length, 8192).Complete();

        dataHash.Append(data);

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        fs.Write(data);

        data.Dispose();
    }

    static int SplatIndexToTextureIndex(uint idx)
    {
        uint2 xy = GaussianUtils.DecodeMorton2D_16x16(idx);
        uint width = GaussianSplatAsset.kTextureWidth / 16;
        idx >>= 8;
        uint x = (idx % width) * 16 + xy.x;
        uint y = (idx / width) * 16 + xy.y;
        return (int)(y * GaussianSplatAsset.kTextureWidth + x);
    }

    [BurstCompile]
    struct CreateColorDataJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<InputSplatData> m_Input;
        [NativeDisableParallelForRestriction] public NativeArray<float4> m_Output;

        public void Execute(int index)
        {
            var splat = m_Input[index];
            int i = SplatIndexToTextureIndex((uint)index);
            m_Output[i] = new float4(splat.dc0.x, splat.dc0.y, splat.dc0.z, splat.opacity);
        }
    }

    void CreateColorData(NativeArray<InputSplatData> inputSplats, string filePath, ref Hash128 dataHash)
    {
        var (width, height) = GaussianSplatAsset.CalcTextureSize(inputSplats.Length);
        NativeArray<float4> data = new(width * height, Allocator.TempJob);

        CreateColorDataJob job = new CreateColorDataJob();
        job.m_Input = inputSplats;
        job.m_Output = data;
        job.Schedule(inputSplats.Length, 8192).Complete();

        dataHash.Append(data);
        dataHash.Append((int)m_FormatColor);

        SaveTex(filePath, width, height, data, m_FormatColor);

        data.Dispose();
    }

    [BurstCompile]
    struct CreateSHDataJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<InputSplatData> m_Input;
        public GaussianSplatAsset.SHFormat m_Format;
        public NativeArray<byte> m_Output;
        public void Execute(int index)
        {
            var splat = m_Input[index];
            switch (m_Format)
            {
                case GaussianSplatAsset.SHFormat.Float16:
                    {
                        GaussianSplatAsset.SHTableItemFloat16 res;
                        res.sh1 = new half3(splat.sh1);
                        res.sh2 = new half3(splat.sh2);
                        res.sh3 = new half3(splat.sh3);
                        res.sh4 = new half3(splat.sh4);
                        res.sh5 = new half3(splat.sh5);
                        res.sh6 = new half3(splat.sh6);
                        res.sh7 = new half3(splat.sh7);
                        res.sh8 = new half3(splat.sh8);
                        res.sh9 = new half3(splat.sh9);
                        res.shA = new half3(splat.shA);
                        res.shB = new half3(splat.shB);
                        res.shC = new half3(splat.shC);
                        res.shD = new half3(splat.shD);
                        res.shE = new half3(splat.shE);
                        res.shF = new half3(splat.shF);
                        res.shPadding = default;
                        var arr = m_Output.Reinterpret<GaussianSplatAsset.SHTableItemFloat16>(1);
                        arr[index] = res;
                    }
                    break;
                case GaussianSplatAsset.SHFormat.Norm11:
                    {
                        GaussianSplatAsset.SHTableItemNorm11 res;
                        res.sh1 = EncodeFloat3ToNorm11(splat.sh1);
                        res.sh2 = EncodeFloat3ToNorm11(splat.sh2);
                        res.sh3 = EncodeFloat3ToNorm11(splat.sh3);
                        res.sh4 = EncodeFloat3ToNorm11(splat.sh4);
                        res.sh5 = EncodeFloat3ToNorm11(splat.sh5);
                        res.sh6 = EncodeFloat3ToNorm11(splat.sh6);
                        res.sh7 = EncodeFloat3ToNorm11(splat.sh7);
                        res.sh8 = EncodeFloat3ToNorm11(splat.sh8);
                        res.sh9 = EncodeFloat3ToNorm11(splat.sh9);
                        res.shA = EncodeFloat3ToNorm11(splat.shA);
                        res.shB = EncodeFloat3ToNorm11(splat.shB);
                        res.shC = EncodeFloat3ToNorm11(splat.shC);
                        res.shD = EncodeFloat3ToNorm11(splat.shD);
                        res.shE = EncodeFloat3ToNorm11(splat.shE);
                        res.shF = EncodeFloat3ToNorm11(splat.shF);
                        var arr = m_Output.Reinterpret<GaussianSplatAsset.SHTableItemNorm11>(1);
                        arr[index] = res;
                    }
                    break;
                case GaussianSplatAsset.SHFormat.Norm6:
                    {
                        GaussianSplatAsset.SHTableItemNorm6 res;
                        res.sh1 = EncodeFloat3ToNorm565(splat.sh1);
                        res.sh2 = EncodeFloat3ToNorm565(splat.sh2);
                        res.sh3 = EncodeFloat3ToNorm565(splat.sh3);
                        res.sh4 = EncodeFloat3ToNorm565(splat.sh4);
                        res.sh5 = EncodeFloat3ToNorm565(splat.sh5);
                        res.sh6 = EncodeFloat3ToNorm565(splat.sh6);
                        res.sh7 = EncodeFloat3ToNorm565(splat.sh7);
                        res.sh8 = EncodeFloat3ToNorm565(splat.sh8);
                        res.sh9 = EncodeFloat3ToNorm565(splat.sh9);
                        res.shA = EncodeFloat3ToNorm565(splat.shA);
                        res.shB = EncodeFloat3ToNorm565(splat.shB);
                        res.shC = EncodeFloat3ToNorm565(splat.shC);
                        res.shD = EncodeFloat3ToNorm565(splat.shD);
                        res.shE = EncodeFloat3ToNorm565(splat.shE);
                        res.shF = EncodeFloat3ToNorm565(splat.shF);
                        res.shPadding = default;
                        var arr = m_Output.Reinterpret<GaussianSplatAsset.SHTableItemNorm6>(1);
                        arr[index] = res;
                    }
                    break;
                default:
                    break;
            }
        }
    }

    static void EmitSimpleDataFile<T>(NativeArray<T> data, string filePath, ref Hash128 dataHash) where T : unmanaged
    {
        dataHash.Append(data);
        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        fs.Write(data.Reinterpret<byte>(UnsafeUtility.SizeOf<T>()));
    }

    void CreateSHData(NativeArray<InputSplatData> inputSplats, string filePath, ref Hash128 dataHash, NativeArray<GaussianSplatAsset.SHTableItemFloat16> clusteredSHs)
    {
        if (clusteredSHs.IsCreated)
        {
            EmitSimpleDataFile(clusteredSHs, filePath, ref dataHash);
        }
        else
        {
            int dataLen = (int)GaussianSplatAsset.CalcSHDataSize(inputSplats.Length, m_FormatSH);
            NativeArray<byte> data = new(dataLen, Allocator.TempJob);
            CreateSHDataJob job = new CreateSHDataJob
            {
                m_Input = inputSplats,
                m_Format = m_FormatSH,
                m_Output = data
            };
            job.Schedule(inputSplats.Length, 8192).Complete();
            EmitSimpleDataFile(data, filePath, ref dataHash);
            data.Dispose();
        }
    }

    static GaussianSplatAsset.CameraInfo[] LoadJsonCamerasFile(string folder)
    {
        string path = $"{folder}/{kCamerasJson}";
        if (!File.Exists(path))
            return null;

        string json = File.ReadAllText(path);
        var jsonCameras = JSONParser.FromJson<List<JsonCamera>>(json);
        if (jsonCameras == null || jsonCameras.Count == 0)
            return null;

        var result = new GaussianSplatAsset.CameraInfo[jsonCameras.Count];
        for (var camIndex = 0; camIndex < jsonCameras.Count; camIndex++)
        {
            var jsonCam = jsonCameras[camIndex];
            var pos = new Vector3(jsonCam.position[0], jsonCam.position[1], jsonCam.position[2]);
            // the matrix is a "view matrix", not "camera matrix" lol
            var axisx = new Vector3(jsonCam.rotation[0][0], jsonCam.rotation[1][0], jsonCam.rotation[2][0]);
            var axisy = new Vector3(jsonCam.rotation[0][1], jsonCam.rotation[1][1], jsonCam.rotation[2][1]);
            var axisz = new Vector3(jsonCam.rotation[0][2], jsonCam.rotation[1][2], jsonCam.rotation[2][2]);

            axisy *= -1;
            axisz *= -1;

            var cam = new GaussianSplatAsset.CameraInfo
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
