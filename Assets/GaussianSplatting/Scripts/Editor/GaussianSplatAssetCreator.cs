// Unity exposes the GraphicsFormats for it, but does not allow to use :(
// it deems them to not be "Sample" usage supported, only because there's no corresponding legacy TextureFormat
// enum for them...
//#define ENABLE_RGB10A2_SUPPORT

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
    const string kPointCloudPly = "point_cloud/iteration_7000/point_cloud.ply";
    const string kPointCloud30kPly = "point_cloud/iteration_30000/point_cloud.ply";
    const string kCamerasJson = "cameras.json";

    enum DataQuality
    {
        Custom,
        VeryLow,
        Low,
        Medium,
        High,
        VeryHigh,
    }

    enum DataFormat
    {
        Float32x4,
        Float16x4,
        #if ENABLE_RGB10A2_SUPPORT
        Norm10_2,
        #endif
        Norm8x4,
        Norm565,
        BC7,
        BC1
    }

    readonly FolderPickerPropertyDrawer m_FolderPicker = new();

    [SerializeField] string m_InputFolder;
    [SerializeField] bool m_Use30k = true;

    [SerializeField] string m_OutputFolder = "Assets/GaussianAssets";
    [SerializeField] DataQuality m_Quality = DataQuality.High;
    [SerializeField] DataFormat m_FormatPos = DataFormat.Float16x4;
    [SerializeField] DataFormat m_FormatRot = DataFormat.Float16x4;
    [SerializeField] DataFormat m_FormatScale = DataFormat.Float16x4;
    [SerializeField] DataFormat m_FormatColor = DataFormat.Norm8x4;
    [SerializeField] DataFormat m_FormatSH = DataFormat.Norm8x4;
    [SerializeField] bool m_ReorderMorton = true;

    string m_ErrorMessage;

    [MenuItem("Tools/Create Gaussian Splat Asset")]
    public static void Init()
    {
        var window = GetWindowWithRect<GaussianSplatAssetCreator>(new Rect(50, 50, 500, 500), false, "Gaussian Splat Creator", true);
        window.minSize = new Vector2(200, 200);
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

        EditorGUILayout.Space();
        GUILayout.Label("Output", EditorStyles.boldLabel);
        rect = EditorGUILayout.GetControlRect(true);
        m_OutputFolder = m_FolderPicker.PathFieldGUI(rect, new GUIContent("Output Folder"), m_OutputFolder, null, "GaussianAssetOutputFolder");
        m_ReorderMorton = EditorGUILayout.Toggle("Morton Reorder", m_ReorderMorton);
        var newQuality = (DataQuality) EditorGUILayout.EnumPopup("Quality", m_Quality);
        if (newQuality != m_Quality)
        {
            m_Quality = newQuality;
            ApplyQualityLevel();
        }
        EditorGUI.BeginDisabledGroup(m_Quality != DataQuality.Custom);
        EditorGUI.indentLevel++;
        m_FormatPos = (DataFormat)EditorGUILayout.EnumPopup("Position", m_FormatPos);
        m_FormatRot = (DataFormat)EditorGUILayout.EnumPopup("Rotation", m_FormatRot);
        m_FormatScale = (DataFormat)EditorGUILayout.EnumPopup("Scale", m_FormatScale);
        m_FormatColor = (DataFormat)EditorGUILayout.EnumPopup("Color", m_FormatColor);
        m_FormatSH = (DataFormat)EditorGUILayout.EnumPopup("SH", m_FormatSH);
        EditorGUI.indentLevel--;
        EditorGUI.EndDisabledGroup();

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
            case DataQuality.VeryLow:
                m_FormatPos = DataFormat.Norm8x4;
                m_FormatRot = DataFormat.Norm8x4;
                m_FormatScale = DataFormat.BC7;
                m_FormatColor = DataFormat.BC7;
                m_FormatSH = DataFormat.BC1;
                break;
            case DataQuality.Low:
                m_FormatPos = DataFormat.Float16x4;
                m_FormatRot = DataFormat.Norm8x4;
                m_FormatScale = DataFormat.Norm8x4;
                m_FormatColor = DataFormat.BC7;
                m_FormatSH = DataFormat.BC7;
                break;
            case DataQuality.Medium:
                m_FormatPos = DataFormat.Float16x4;
                m_FormatRot = DataFormat.Float16x4;
                m_FormatScale = DataFormat.Float16x4;
                m_FormatColor = DataFormat.Norm8x4;
                m_FormatSH = DataFormat.BC7;
                break;
            case DataQuality.High:
                m_FormatPos = DataFormat.Float16x4;
                m_FormatRot = DataFormat.Float16x4;
                m_FormatScale = DataFormat.Float16x4;
                m_FormatColor = DataFormat.Float16x4;
                m_FormatSH = DataFormat.Norm8x4;
                break;
            case DataQuality.VeryHigh:
                m_FormatPos = DataFormat.Float32x4;
                m_FormatRot = DataFormat.Float32x4;
                m_FormatScale = DataFormat.Float32x4;
                m_FormatColor = DataFormat.Float32x4;
                m_FormatSH = DataFormat.Float32x4;
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

        EditorUtility.DisplayProgressBar("Creating Gaussian Splat Asset", "Reading cameras info", 0.0f);
        GaussianSplatAsset.CameraInfo[] cameras = LoadJsonCamerasFile(m_InputFolder);

        EditorUtility.DisplayProgressBar("Creating Gaussian Splat Asset", "Reading PLY file", 0.1f);
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

        if (m_ReorderMorton)
        {
            EditorUtility.DisplayProgressBar("Creating Gaussian Splat Asset", "Morton reordering", 0.2f);
            ReorderMorton(inputSplats, boundsMin, boundsMax);
        }

        string baseName = Path.GetFileNameWithoutExtension(m_InputFolder) + (m_Use30k ? "_30k" : "_7k");

        GaussianSplatAsset asset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
        asset.name = baseName;
        asset.m_Cameras = cameras;
        asset.m_BoundsMin = boundsMin;
        asset.m_BoundsMax = boundsMax;

        EditorUtility.DisplayProgressBar("Creating Gaussian Splat Asset", "Calc chunks", 0.3f);
        LinearizeData(inputSplats);
        asset.m_Chunks = CalcChunkData(inputSplats);

        EditorUtility.DisplayProgressBar("Creating Gaussian Splat Asset", "Creating texture objects", 0.4f);
        AssetDatabase.StartAssetEditing();
        List<string> imageFiles = CreateTextureFiles(inputSplats, asset, baseName);

        // files are created, import them so we can get to the imported objects, ugh
        EditorUtility.DisplayProgressBar("Creating Gaussian Splat Asset", "Initial texture import", 0.5f);
        AssetDatabase.StopAssetEditing();
        AssetDatabase.Refresh(ImportAssetOptions.ForceUncompressedImport);

        EditorUtility.DisplayProgressBar("Creating Gaussian Splat Asset", "Setup textures onto asset", 0.8f);
        for (int i = 0; i < imageFiles.Count; ++i)
            asset.m_Tex[i] = AssetDatabase.LoadAssetAtPath<Texture2D>(imageFiles[i]);

        var assetPath = $"{m_OutputFolder}/{baseName}.asset";
        var savedAsset = CreateOrReplaceAsset(asset, assetPath);

        EditorUtility.DisplayProgressBar("Creating Gaussian Splat Asset", "Saving assets", 0.9f);
        AssetDatabase.SaveAssets();
        EditorUtility.ClearProgressBar();

        Selection.activeObject = savedAsset;
    }

    unsafe NativeArray<InputSplatData> LoadPLYSplatFile(string folder, bool use30k)
    {
        NativeArray<InputSplatData> data = default;
        string plyPath = $"{folder}/{(use30k ? kPointCloud30kPly : kPointCloudPly)}";
        if (!File.Exists(plyPath))
        {
            plyPath = $"{folder}/{kPointCloudPly}";
            if (!File.Exists(plyPath))
            {
                m_ErrorMessage = $"Did not find {plyPath} file";
                return data;
            }
        }

        int splatCount = 0;
        PLYFileReader.ReadFile(plyPath, out splatCount, out int vertexStride, out var plyAttrNames, out var verticesRawData);
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

            // color
            splat.dc0 = GaussianUtils.SH0ToColor(splat.dc0);
            splat.opacity = GaussianUtils.Sigmoid(splat.opacity);

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
            chunkMin.sh1 = (float3) float.PositiveInfinity;
            chunkMin.sh2 = (float3) float.PositiveInfinity;
            chunkMin.sh3 = (float3) float.PositiveInfinity;
            chunkMin.sh4 = (float3) float.PositiveInfinity;
            chunkMin.sh5 = (float3) float.PositiveInfinity;
            chunkMin.sh6 = (float3) float.PositiveInfinity;
            chunkMin.sh7 = (float3) float.PositiveInfinity;
            chunkMin.sh8 = (float3) float.PositiveInfinity;
            chunkMin.sh9 = (float3) float.PositiveInfinity;
            chunkMin.shA = (float3) float.PositiveInfinity;
            chunkMin.shB = (float3) float.PositiveInfinity;
            chunkMin.shC = (float3) float.PositiveInfinity;
            chunkMin.shD = (float3) float.PositiveInfinity;
            chunkMin.shE = (float3) float.PositiveInfinity;
            chunkMin.shF = (float3) float.PositiveInfinity;
            GaussianSplatAsset.BoundsInfo chunkMax;
            chunkMax.pos = (float3) float.NegativeInfinity;
            chunkMax.scl = (float3) float.NegativeInfinity;
            chunkMax.col = (float4) float.NegativeInfinity;
            chunkMax.sh1 = (float3) float.NegativeInfinity;
            chunkMax.sh2 = (float3) float.NegativeInfinity;
            chunkMax.sh3 = (float3) float.NegativeInfinity;
            chunkMax.sh4 = (float3) float.NegativeInfinity;
            chunkMax.sh5 = (float3) float.NegativeInfinity;
            chunkMax.sh6 = (float3) float.NegativeInfinity;
            chunkMax.sh7 = (float3) float.NegativeInfinity;
            chunkMax.sh8 = (float3) float.NegativeInfinity;
            chunkMax.sh9 = (float3) float.NegativeInfinity;
            chunkMax.shA = (float3) float.NegativeInfinity;
            chunkMax.shB = (float3) float.NegativeInfinity;
            chunkMax.shC = (float3) float.NegativeInfinity;
            chunkMax.shD = (float3) float.NegativeInfinity;
            chunkMax.shE = (float3) float.NegativeInfinity;
            chunkMax.shF = (float3) float.NegativeInfinity;

            int splatBegin = math.min(chunkIdx * GaussianSplatAsset.kChunkSize, splatData.Length);
            int splatEnd = math.min((chunkIdx + 1) * GaussianSplatAsset.kChunkSize, splatData.Length);

            // calculate data bounds inside the chunk
            for (int i = splatBegin; i < splatEnd; ++i)
            {
                InputSplatData s = splatData[i];
                chunkMin.pos = math.min(chunkMin.pos, s.pos);
                chunkMin.scl = math.min(chunkMin.scl, s.scale);
                chunkMin.col = math.min(chunkMin.col, new float4(s.dc0, s.opacity));
                chunkMin.sh1 = math.min(chunkMin.sh1, s.sh1);
                chunkMin.sh2 = math.min(chunkMin.sh2, s.sh2);
                chunkMin.sh3 = math.min(chunkMin.sh3, s.sh3);
                chunkMin.sh4 = math.min(chunkMin.sh4, s.sh4);
                chunkMin.sh5 = math.min(chunkMin.sh5, s.sh5);
                chunkMin.sh6 = math.min(chunkMin.sh6, s.sh6);
                chunkMin.sh7 = math.min(chunkMin.sh7, s.sh7);
                chunkMin.sh8 = math.min(chunkMin.sh8, s.sh8);
                chunkMin.sh9 = math.min(chunkMin.sh9, s.sh9);
                chunkMin.shA = math.min(chunkMin.shA, s.shA);
                chunkMin.shB = math.min(chunkMin.shB, s.shB);
                chunkMin.shC = math.min(chunkMin.shC, s.shC);
                chunkMin.shD = math.min(chunkMin.shD, s.shD);
                chunkMin.shE = math.min(chunkMin.shE, s.shE);
                chunkMin.shF = math.min(chunkMin.shF, s.shF);

                chunkMax.pos = math.max(chunkMax.pos, s.pos);
                chunkMax.scl = math.max(chunkMax.scl, s.scale);
                chunkMax.col = math.max(chunkMax.col, new float4(s.dc0, s.opacity));
                chunkMax.sh1 = math.max(chunkMax.sh1, s.sh1);
                chunkMax.sh2 = math.max(chunkMax.sh2, s.sh2);
                chunkMax.sh3 = math.max(chunkMax.sh3, s.sh3);
                chunkMax.sh4 = math.max(chunkMax.sh4, s.sh4);
                chunkMax.sh5 = math.max(chunkMax.sh5, s.sh5);
                chunkMax.sh6 = math.max(chunkMax.sh6, s.sh6);
                chunkMax.sh7 = math.max(chunkMax.sh7, s.sh7);
                chunkMax.sh8 = math.max(chunkMax.sh8, s.sh8);
                chunkMax.sh9 = math.max(chunkMax.sh9, s.sh9);
                chunkMax.shA = math.max(chunkMax.shA, s.shA);
                chunkMax.shB = math.max(chunkMax.shB, s.shB);
                chunkMax.shC = math.max(chunkMax.shC, s.shC);
                chunkMax.shD = math.max(chunkMax.shD, s.shD);
                chunkMax.shE = math.max(chunkMax.shE, s.shE);
                chunkMax.shF = math.max(chunkMax.shF, s.shF);
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
                s.sh1 = (s.sh1 - chunkMin.sh1) / (float3)(chunkMax.sh1 - chunkMin.sh1);
                s.sh2 = (s.sh2 - chunkMin.sh2) / (float3)(chunkMax.sh2 - chunkMin.sh2);
                s.sh3 = (s.sh3 - chunkMin.sh3) / (float3)(chunkMax.sh3 - chunkMin.sh3);
                s.sh4 = (s.sh4 - chunkMin.sh4) / (float3)(chunkMax.sh4 - chunkMin.sh4);
                s.sh5 = (s.sh5 - chunkMin.sh5) / (float3)(chunkMax.sh5 - chunkMin.sh5);
                s.sh6 = (s.sh6 - chunkMin.sh6) / (float3)(chunkMax.sh6 - chunkMin.sh6);
                s.sh7 = (s.sh7 - chunkMin.sh7) / (float3)(chunkMax.sh7 - chunkMin.sh7);
                s.sh8 = (s.sh8 - chunkMin.sh8) / (float3)(chunkMax.sh8 - chunkMin.sh8);
                s.sh9 = (s.sh9 - chunkMin.sh9) / (float3)(chunkMax.sh9 - chunkMin.sh9);
                s.shA = (s.shA - chunkMin.shA) / (float3)(chunkMax.shA - chunkMin.shA);
                s.shB = (s.shB - chunkMin.shB) / (float3)(chunkMax.shB - chunkMin.shB);
                s.shC = (s.shC - chunkMin.shC) / (float3)(chunkMax.shC - chunkMin.shC);
                s.shD = (s.shD - chunkMin.shD) / (float3)(chunkMax.shD - chunkMin.shD);
                s.shE = (s.shE - chunkMin.shE) / (float3)(chunkMax.shE - chunkMin.shE);
                s.shF = (s.shF - chunkMin.shF) / (float3)(chunkMax.shF - chunkMin.shF);
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

    [BurstCompile]
    public struct InitTextureDataJob : IJobParallelFor
    {
        public int width, height;
        public NativeArray<float3> dataPos;
        public NativeArray<float4> dataRot;
        public NativeArray<float3> dataScl;
        public NativeArray<float4> dataCol;
        public NativeArray<float3> dataSh1;
        public NativeArray<float3> dataSh2;
        public NativeArray<float3> dataSh3;
        public NativeArray<float3> dataSh4;
        public NativeArray<float3> dataSh5;
        public NativeArray<float3> dataSh6;
        public NativeArray<float3> dataSh7;
        public NativeArray<float3> dataSh8;
        public NativeArray<float3> dataSh9;
        public NativeArray<float3> dataShA;
        public NativeArray<float3> dataShB;
        public NativeArray<float3> dataShC;
        public NativeArray<float3> dataShD;
        public NativeArray<float3> dataShE;
        public NativeArray<float3> dataShF;

        [ReadOnly] NativeArray<InputSplatData> inputSplats;

        public InitTextureDataJob(NativeArray<InputSplatData> input)
        {
            inputSplats = input;

            const int kTextureWidth = 2048; //@TODO: bump to 4k
            width = kTextureWidth;
            height = math.max(1, (input.Length + width - 1) / width);
            // height multiple of compressed block heights
            int blockHeight = 4;
            height = (height + blockHeight - 1) / blockHeight * blockHeight;

            dataPos = new NativeArray<float3>(width * height, Allocator.Persistent);
            dataRot = new NativeArray<float4>(width * height, Allocator.Persistent);
            dataScl = new NativeArray<float3>(width * height, Allocator.Persistent);
            dataCol = new NativeArray<float4>(width * height, Allocator.Persistent);
            dataSh1 = new NativeArray<float3>(width * height, Allocator.Persistent);
            dataSh2 = new NativeArray<float3>(width * height, Allocator.Persistent);
            dataSh3 = new NativeArray<float3>(width * height, Allocator.Persistent);
            dataSh4 = new NativeArray<float3>(width * height, Allocator.Persistent);
            dataSh5 = new NativeArray<float3>(width * height, Allocator.Persistent);
            dataSh6 = new NativeArray<float3>(width * height, Allocator.Persistent);
            dataSh7 = new NativeArray<float3>(width * height, Allocator.Persistent);
            dataSh8 = new NativeArray<float3>(width * height, Allocator.Persistent);
            dataSh9 = new NativeArray<float3>(width * height, Allocator.Persistent);
            dataShA = new NativeArray<float3>(width * height, Allocator.Persistent);
            dataShB = new NativeArray<float3>(width * height, Allocator.Persistent);
            dataShC = new NativeArray<float3>(width * height, Allocator.Persistent);
            dataShD = new NativeArray<float3>(width * height, Allocator.Persistent);
            dataShE = new NativeArray<float3>(width * height, Allocator.Persistent);
            dataShF = new NativeArray<float3>(width * height, Allocator.Persistent);
        }

        public void Dispose()
        {
            dataPos.Dispose();
            dataRot.Dispose();
            dataScl.Dispose();
            dataCol.Dispose();
            dataSh1.Dispose();
            dataSh2.Dispose();
            dataSh3.Dispose();
            dataSh4.Dispose();
            dataSh5.Dispose();
            dataSh6.Dispose();
            dataSh7.Dispose();
            dataSh8.Dispose();
            dataSh9.Dispose();
            dataShA.Dispose();
            dataShB.Dispose();
            dataShC.Dispose();
            dataShD.Dispose();
            dataShE.Dispose();
            dataShF.Dispose();
        }

        public void Execute(int i)
        {
            var splat = inputSplats[i];

            // pos
            float3 pos = splat.pos;
            dataPos[i] = pos;

            // rot
            var q = splat.rot;
            dataRot[i] = new float4(q.x, q.y, q.z, q.w);

            // scale
            dataScl[i] = splat.scale;

            // color
            var c = splat.dc0;
            var a = splat.opacity;
            dataCol[i] = new float4(c.x, c.y, c.z, a);

            // SHs
            dataSh1[i] = splat.sh1;
            dataSh2[i] = splat.sh2;
            dataSh3[i] = splat.sh3;
            dataSh4[i] = splat.sh4;
            dataSh5[i] = splat.sh5;
            dataSh6[i] = splat.sh6;
            dataSh7[i] = splat.sh7;
            dataSh8[i] = splat.sh8;
            dataSh9[i] = splat.sh9;
            dataShA[i] = splat.shA;
            dataShB[i] = splat.shB;
            dataShC[i] = splat.shC;
            dataShD[i] = splat.shD;
            dataShE[i] = splat.shE;
            dataShF[i] = splat.shF;
        }
    }

    static GraphicsFormat DataFormatToGraphics(DataFormat format)
    {
        return format switch
        {
            DataFormat.Float32x4 => GraphicsFormat.R32G32B32A32_SFloat,
            DataFormat.Float16x4 => GraphicsFormat.R16G16B16A16_SFloat,
            #if ENABLE_RGB10A2_SUPPORT
            DataFormat.Norm10_2 => GraphicsFormat.A2B10G10R10_UNormPack32,
            #endif
            DataFormat.Norm8x4 => GraphicsFormat.R8G8B8A8_UNorm,
            DataFormat.Norm565 => GraphicsFormat.B5G6R5_UNormPack16,
            DataFormat.BC7 => GraphicsFormat.RGBA_BC7_UNorm,
            DataFormat.BC1 => GraphicsFormat.RGBA_DXT1_UNorm,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
        };
    }

    [BurstCompile]
    struct ConvertDataJob : IJobParallelFor
    {
        public int width, height, channels;
        [ReadOnly] public NativeArray<float> inputData;
        [NativeDisableParallelForRestriction] public NativeArray<byte> outputData;
        public DataFormat format;
        public int formatBytesPerPixel;

        public unsafe void Execute(int y)
        {
            int srcIdx = y * width * channels;
            byte* dstPtr = (byte*) outputData.GetUnsafePtr() + y * width * formatBytesPerPixel;
            for (int x = 0; x < width; ++x)
            {
                float4 pix = 1.0f;
                pix.x = inputData[srcIdx + 0];
                pix.y = inputData[srcIdx + 1];
                pix.z = inputData[srcIdx + 2];
                if (channels == 4)
                    pix.w = inputData[srcIdx + 3];

                switch (format)
                {
                    case DataFormat.Float32x4:
                        *(float4*) dstPtr = pix;
                        break;
                    case DataFormat.Float16x4:
                    {
                        half4 enc = new half4(pix);
                        *(half4*) dstPtr = enc;
                    }
                        break;
                    #if ENABLE_RGB10A2_SUPPORT
                    case DataFormat.Norm10_2:
                    {
                        pix = math.saturate(pix);
                        uint enc = (uint)(pix.x * 1023.5f) | ((uint)(pix.y * 1023.5f) << 10) | ((uint)(pix.z * 1023.5f) << 20) | ((uint)(pix.w * 3.5f) << 30);
                        *(uint*) dstPtr = enc;
                    }
                        break;
                    #endif
                    case DataFormat.Norm8x4:
                    {
                        pix = math.saturate(pix);
                        uint enc = (uint)(pix.x * 255.5f) | ((uint)(pix.y * 255.5f) << 8) | ((uint)(pix.z * 255.5f) << 16) | ((uint)(pix.w * 255.5f) << 24);
                        *(uint*) dstPtr = enc;
                    }
                        break;
                    case DataFormat.Norm565:
                    {
                        pix = math.saturate(pix);
                        uint enc = ((uint)(pix.x * 31.5f) << 11) | ((uint)(pix.y * 63.5f) << 5) | (uint)(pix.z * 31.5f);
                        *(ushort*) dstPtr = (ushort)enc;
                    }
                        break;
                }

                srcIdx += channels;
                dstPtr += formatBytesPerPixel;
            }
        }
    }

    static string SaveTex(string path, int width, int height, NativeArray<float> data, int channels, DataFormat format)
    {
        GraphicsFormat gfxFormat = DataFormatToGraphics(format);
        int dstSize = (int)GraphicsFormatUtility.ComputeMipmapSize(width, height, gfxFormat);

        if (GraphicsFormatUtility.IsCompressedFormat(gfxFormat))
        {
            Texture2D tex = new Texture2D(width, height, channels == 4 ? GraphicsFormat.R32G32B32A32_SFloat : GraphicsFormat.R32G32B32_SFloat, TextureCreationFlags.DontInitializePixels | TextureCreationFlags.DontUploadUponCreate);
            tex.SetPixelData(data, 0);
            EditorUtility.CompressTexture(tex, GraphicsFormatUtility.GetTextureFormat(gfxFormat), TextureCompressionQuality.Normal);
            NativeArray<byte> cmpData = tex.GetPixelData<byte>(0);
            GaussianTexImporter.WriteAsset(width, height, gfxFormat, cmpData.AsReadOnlySpan(), path);
            DestroyImmediate(tex);
        }
        else
        {
            ConvertDataJob job = new ConvertDataJob
            {
                width = width,
                height = height,
                channels = channels,
                inputData = data,
                format = format,
                outputData = new NativeArray<byte>(dstSize, Allocator.TempJob),
                formatBytesPerPixel = dstSize / width / height
            };
            job.Schedule(height, 1).Complete();
            GaussianTexImporter.WriteAsset(width, height, gfxFormat, job.outputData.AsReadOnlySpan(), path);
            job.outputData.Dispose();
        }
        return path;
    }

    List<string> CreateTextureFiles(NativeArray<InputSplatData> inputSplats, GaussianSplatAsset asset, string baseName)
    {
        InitTextureDataJob texData = new InitTextureDataJob(inputSplats);
        texData.Schedule(inputSplats.Length, 4096).Complete();
        asset.m_SplatCount = inputSplats.Length;

        List<string> imageFiles = new();
        imageFiles.Add(SaveTex($"{m_OutputFolder}/{baseName}_pos.gstex", texData.width, texData.height, texData.dataPos.Reinterpret<float>(12), 3, m_FormatPos));
        imageFiles.Add(SaveTex($"{m_OutputFolder}/{baseName}_rot.gstex", texData.width, texData.height, texData.dataRot.Reinterpret<float>(16), 4, m_FormatRot));
        imageFiles.Add(SaveTex($"{m_OutputFolder}/{baseName}_scl.gstex", texData.width, texData.height, texData.dataScl.Reinterpret<float>(12), 3, m_FormatScale));
        imageFiles.Add(SaveTex($"{m_OutputFolder}/{baseName}_col.gstex", texData.width, texData.height, texData.dataCol.Reinterpret<float>(16), 4, m_FormatColor));
        imageFiles.Add(SaveTex($"{m_OutputFolder}/{baseName}_sh1.gstex", texData.width, texData.height, texData.dataSh1.Reinterpret<float>(12), 3, m_FormatSH));
        imageFiles.Add(SaveTex($"{m_OutputFolder}/{baseName}_sh2.gstex", texData.width, texData.height, texData.dataSh2.Reinterpret<float>(12), 3, m_FormatSH));
        imageFiles.Add(SaveTex($"{m_OutputFolder}/{baseName}_sh3.gstex", texData.width, texData.height, texData.dataSh3.Reinterpret<float>(12), 3, m_FormatSH));
        imageFiles.Add(SaveTex($"{m_OutputFolder}/{baseName}_sh4.gstex", texData.width, texData.height, texData.dataSh4.Reinterpret<float>(12), 3, m_FormatSH));
        imageFiles.Add(SaveTex($"{m_OutputFolder}/{baseName}_sh5.gstex", texData.width, texData.height, texData.dataSh5.Reinterpret<float>(12), 3, m_FormatSH));
        imageFiles.Add(SaveTex($"{m_OutputFolder}/{baseName}_sh6.gstex", texData.width, texData.height, texData.dataSh6.Reinterpret<float>(12), 3, m_FormatSH));
        imageFiles.Add(SaveTex($"{m_OutputFolder}/{baseName}_sh7.gstex", texData.width, texData.height, texData.dataSh7.Reinterpret<float>(12), 3, m_FormatSH));
        imageFiles.Add(SaveTex($"{m_OutputFolder}/{baseName}_sh8.gstex", texData.width, texData.height, texData.dataSh8.Reinterpret<float>(12), 3, m_FormatSH));
        imageFiles.Add(SaveTex($"{m_OutputFolder}/{baseName}_sh9.gstex", texData.width, texData.height, texData.dataSh9.Reinterpret<float>(12), 3, m_FormatSH));
        imageFiles.Add(SaveTex($"{m_OutputFolder}/{baseName}_sha.gstex", texData.width, texData.height, texData.dataShA.Reinterpret<float>(12), 3, m_FormatSH));
        imageFiles.Add(SaveTex($"{m_OutputFolder}/{baseName}_shb.gstex", texData.width, texData.height, texData.dataShB.Reinterpret<float>(12), 3, m_FormatSH));
        imageFiles.Add(SaveTex($"{m_OutputFolder}/{baseName}_shc.gstex", texData.width, texData.height, texData.dataShC.Reinterpret<float>(12), 3, m_FormatSH));
        imageFiles.Add(SaveTex($"{m_OutputFolder}/{baseName}_shd.gstex", texData.width, texData.height, texData.dataShD.Reinterpret<float>(12), 3, m_FormatSH));
        imageFiles.Add(SaveTex($"{m_OutputFolder}/{baseName}_she.gstex", texData.width, texData.height, texData.dataShE.Reinterpret<float>(12), 3, m_FormatSH));
        imageFiles.Add(SaveTex($"{m_OutputFolder}/{baseName}_shf.gstex", texData.width, texData.height, texData.dataShF.Reinterpret<float>(12), 3, m_FormatSH));

        texData.Dispose();
        return imageFiles;
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

            pos.z *= -1;
            axisy *= -1;
            axisx.z *= -1;
            axisy.z *= -1;
            axisz.z *= -1;

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
