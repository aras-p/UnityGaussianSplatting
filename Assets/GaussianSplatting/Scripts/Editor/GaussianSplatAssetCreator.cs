using System;
using System.Collections.Generic;
using System.IO;
using TinyJson;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

public class GaussianSplatAssetCreator : EditorWindow
{
    const string kPointCloudPly = "point_cloud/iteration_7000/point_cloud.ply";
    const string kPointCloud30kPly = "point_cloud/iteration_30000/point_cloud.ply";
    const string kCamerasJson = "cameras.json";

    readonly FolderPickerPropertyDrawer m_FolderPicker = new();

    [SerializeField] string m_InputFolder;
    [SerializeField] bool m_Use30k = true;

    [SerializeField] string m_OutputFolder = "Assets/GaussianAssets";

    string m_ErrorMessage;

    [MenuItem("Tools/Create Gaussian Splat Asset")]
    public static void Init()
    {
        var window = GetWindowWithRect<GaussianSplatAssetCreator>(new Rect(50, 50, 500, 500), false, "Gaussian Splat Creator", true);
        window.minSize = new Vector2(200, 200);
        window.maxSize = new Vector2(1500, 1500);
        window.Show();
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

    void CreateAsset()
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

        int splatCount = inputSplats.Length;

        string baseName = Path.GetFileNameWithoutExtension(m_InputFolder) + (m_Use30k ? "_30k" : "_7k");

        var assetPath = $"{m_OutputFolder}/{baseName}.asset";

        EditorUtility.DisplayProgressBar("Creating Gaussian Splat Asset", "Creating texture objects", 0.2f);
        AssetDatabase.StartAssetEditing();
        var texPos = CreateTexture($"{baseName}_pos", splatCount, GraphicsFormat.R32G32B32_SFloat);
        var texRot = CreateTexture($"{baseName}_rot", splatCount, GraphicsFormat.R32G32B32A32_SFloat);
        var texScl = CreateTexture($"{baseName}_scl", splatCount, GraphicsFormat.R32G32B32_SFloat);
        var texCol = CreateTexture($"{baseName}_col", splatCount, GraphicsFormat.R32G32B32A32_SFloat);
        var texsh1 = CreateTexture($"{baseName}_sh1", splatCount, GraphicsFormat.R32G32B32_SFloat);
        var texsh2 = CreateTexture($"{baseName}_sh2", splatCount, GraphicsFormat.R32G32B32_SFloat);
        var texsh3 = CreateTexture($"{baseName}_sh3", splatCount, GraphicsFormat.R32G32B32_SFloat);
        var texsh4 = CreateTexture($"{baseName}_sh4", splatCount, GraphicsFormat.R32G32B32_SFloat);
        var texsh5 = CreateTexture($"{baseName}_sh5", splatCount, GraphicsFormat.R32G32B32_SFloat);
        var texsh6 = CreateTexture($"{baseName}_sh6", splatCount, GraphicsFormat.R32G32B32_SFloat);
        var texsh7 = CreateTexture($"{baseName}_sh7", splatCount, GraphicsFormat.R32G32B32_SFloat);
        var texsh8 = CreateTexture($"{baseName}_sh8", splatCount, GraphicsFormat.R32G32B32_SFloat);
        var texsh9 = CreateTexture($"{baseName}_sh9", splatCount, GraphicsFormat.R32G32B32_SFloat);
        var texshA = CreateTexture($"{baseName}_shA", splatCount, GraphicsFormat.R32G32B32_SFloat);
        var texshB = CreateTexture($"{baseName}_shB", splatCount, GraphicsFormat.R32G32B32_SFloat);
        var texshC = CreateTexture($"{baseName}_shC", splatCount, GraphicsFormat.R32G32B32_SFloat);
        var texshD = CreateTexture($"{baseName}_shD", splatCount, GraphicsFormat.R32G32B32_SFloat);
        var texshE = CreateTexture($"{baseName}_shE", splatCount, GraphicsFormat.R32G32B32_SFloat);
        var texshF = CreateTexture($"{baseName}_shF", splatCount, GraphicsFormat.R32G32B32_SFloat);

        GaussianSplatAsset asset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
        asset.name = baseName;
        asset.m_Cameras = cameras;
        asset.m_TexPos = texPos;
        asset.m_TexRot = texRot;
        asset.m_TexScl = texScl;
        asset.m_TexCol = texCol;
        asset.m_TexSH1 = texsh1;
        asset.m_TexSH2 = texsh2;
        asset.m_TexSH3 = texsh3;
        asset.m_TexSH4 = texsh4;
        asset.m_TexSH5 = texsh5;
        asset.m_TexSH6 = texsh6;
        asset.m_TexSH7 = texsh7;
        asset.m_TexSH8 = texsh8;
        asset.m_TexSH9 = texsh9;
        asset.m_TexSHA = texshA;
        asset.m_TexSHB = texshB;
        asset.m_TexSHC = texshC;
        asset.m_TexSHD = texshD;
        asset.m_TexSHE = texshE;
        asset.m_TexSHF = texshF;

        EditorUtility.DisplayProgressBar("Creating Gaussian Splat Asset", "Setting up initial data", 0.3f);
        InitAssetData(inputSplats, asset);

        EditorUtility.DisplayProgressBar("Creating Gaussian Splat Asset", "Serializing textures", 0.8f);
        asset.m_TexPos = CreateOrReplaceTex(m_OutputFolder, texPos);
        asset.m_TexRot = CreateOrReplaceTex(m_OutputFolder, texRot);
        asset.m_TexScl = CreateOrReplaceTex(m_OutputFolder, texScl);
        asset.m_TexCol = CreateOrReplaceTex(m_OutputFolder, texCol);
        asset.m_TexSH1 = CreateOrReplaceTex(m_OutputFolder, texsh1);
        asset.m_TexSH2 = CreateOrReplaceTex(m_OutputFolder, texsh2);
        asset.m_TexSH3 = CreateOrReplaceTex(m_OutputFolder, texsh3);
        asset.m_TexSH4 = CreateOrReplaceTex(m_OutputFolder, texsh4);
        asset.m_TexSH5 = CreateOrReplaceTex(m_OutputFolder, texsh5);
        asset.m_TexSH6 = CreateOrReplaceTex(m_OutputFolder, texsh6);
        asset.m_TexSH7 = CreateOrReplaceTex(m_OutputFolder, texsh7);
        asset.m_TexSH8 = CreateOrReplaceTex(m_OutputFolder, texsh8);
        asset.m_TexSH9 = CreateOrReplaceTex(m_OutputFolder, texsh9);
        asset.m_TexSHA = CreateOrReplaceTex(m_OutputFolder, texshA);
        asset.m_TexSHB = CreateOrReplaceTex(m_OutputFolder, texshB);
        asset.m_TexSHC = CreateOrReplaceTex(m_OutputFolder, texshC);
        asset.m_TexSHD = CreateOrReplaceTex(m_OutputFolder, texshD);
        asset.m_TexSHE = CreateOrReplaceTex(m_OutputFolder, texshE);
        asset.m_TexSHF = CreateOrReplaceTex(m_OutputFolder, texshF);
        var savedAsset = CreateOrReplaceAsset(asset, assetPath);

        EditorUtility.DisplayProgressBar("Creating Gaussian Splat Asset", "Saving assets", 0.9f);
        AssetDatabase.StopAssetEditing();
        AssetDatabase.SaveAssets();
        EditorUtility.ClearProgressBar();

        Selection.activeObject = savedAsset;
    }

    static Texture2D CreateOrReplaceTex(string folder, Texture2D tex)
    {
        tex.Apply(false, true); // removes "is readable" flag
        return CreateOrReplaceAsset(tex, $"{folder}/{tex.name}.texture2D");
    }

    static Texture2D CreateTexture(string name, int splatCount, GraphicsFormat format)
    {
        const int kTextureWidth = 2048; //@TODO: bump to 4k
        int width = kTextureWidth;
        int height = math.max(1, (splatCount + width - 1) / width);
        // adjust height to multiple of block size for compressed formats
        if (GraphicsFormatUtility.IsCompressedFormat(format))
        {
            int blockHeight = (int)GraphicsFormatUtility.GetBlockHeight(format);
            height = (height + blockHeight - 1) / blockHeight * blockHeight;
        }

        var tex = new Texture2D(width, height, format, TextureCreationFlags.DontUploadUponCreate);
        tex.name = name;
        return tex;
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

    void InitAssetData(NativeArray<InputSplatData> inputSplats, GaussianSplatAsset asset)
    {
        asset.m_SplatCount = inputSplats.Length;
        NativeArray<float3> f3data = new(asset.m_TexPos.width * asset.m_TexPos.height, Allocator.TempJob);
        NativeArray<float4> f4data = new(asset.m_TexPos.width * asset.m_TexPos.height, Allocator.TempJob);
        // pos
        asset.m_BoundsMin = (float3)float.PositiveInfinity;
        asset.m_BoundsMax = (float3)float.NegativeInfinity;
        for (int i = 0; i < asset.m_SplatCount; ++i)
        {
            float3 pos = inputSplats[i].pos;
            asset.m_BoundsMin = math.min(asset.m_BoundsMin, pos);
            asset.m_BoundsMax = math.max(asset.m_BoundsMax, pos);
            f3data[i] = pos;
        }

        asset.m_TexPos.SetPixelData(f3data, 0);
        // rot
        for (int i = 0; i < asset.m_SplatCount; ++i)
        {
            var q = inputSplats[i].rot;
            f4data[i] = GaussianUtils.NormalizeSwizzleRotation(new float4(q.x, q.y, q.z, q.w));
        }
        asset.m_TexRot.SetPixelData(f4data, 0);
        // scale
        for (int i = 0; i < asset.m_SplatCount; ++i)
            f3data[i] = GaussianUtils.LinearScale(inputSplats[i].scale);
        asset.m_TexScl.SetPixelData(f3data, 0);
        // color
        for (int i = 0; i < asset.m_SplatCount; ++i)
        {
            var c = GaussianUtils.SH0ToColor(inputSplats[i].dc0);
            var a = GaussianUtils.Sigmoid(inputSplats[i].opacity);
            f4data[i] = new float4(c.x, c.y, c.z, a);
        }
        asset.m_TexCol.SetPixelData(f4data, 0);
        // SHs
        for (int i = 0; i < asset.m_SplatCount; ++i) f3data[i] = inputSplats[i].sh1; asset.m_TexSH1.SetPixelData(f3data, 0);
        for (int i = 0; i < asset.m_SplatCount; ++i) f3data[i] = inputSplats[i].sh2; asset.m_TexSH2.SetPixelData(f3data, 0);
        for (int i = 0; i < asset.m_SplatCount; ++i) f3data[i] = inputSplats[i].sh3; asset.m_TexSH3.SetPixelData(f3data, 0);
        for (int i = 0; i < asset.m_SplatCount; ++i) f3data[i] = inputSplats[i].sh4; asset.m_TexSH4.SetPixelData(f3data, 0);
        for (int i = 0; i < asset.m_SplatCount; ++i) f3data[i] = inputSplats[i].sh5; asset.m_TexSH5.SetPixelData(f3data, 0);
        for (int i = 0; i < asset.m_SplatCount; ++i) f3data[i] = inputSplats[i].sh6; asset.m_TexSH6.SetPixelData(f3data, 0);
        for (int i = 0; i < asset.m_SplatCount; ++i) f3data[i] = inputSplats[i].sh7; asset.m_TexSH7.SetPixelData(f3data, 0);
        for (int i = 0; i < asset.m_SplatCount; ++i) f3data[i] = inputSplats[i].sh8; asset.m_TexSH8.SetPixelData(f3data, 0);
        for (int i = 0; i < asset.m_SplatCount; ++i) f3data[i] = inputSplats[i].sh9; asset.m_TexSH9.SetPixelData(f3data, 0);
        for (int i = 0; i < asset.m_SplatCount; ++i) f3data[i] = inputSplats[i].shA; asset.m_TexSHA.SetPixelData(f3data, 0);
        for (int i = 0; i < asset.m_SplatCount; ++i) f3data[i] = inputSplats[i].shB; asset.m_TexSHB.SetPixelData(f3data, 0);
        for (int i = 0; i < asset.m_SplatCount; ++i) f3data[i] = inputSplats[i].shC; asset.m_TexSHC.SetPixelData(f3data, 0);
        for (int i = 0; i < asset.m_SplatCount; ++i) f3data[i] = inputSplats[i].shD; asset.m_TexSHD.SetPixelData(f3data, 0);
        for (int i = 0; i < asset.m_SplatCount; ++i) f3data[i] = inputSplats[i].shE; asset.m_TexSHE.SetPixelData(f3data, 0);
        for (int i = 0; i < asset.m_SplatCount; ++i) f3data[i] = inputSplats[i].shF; asset.m_TexSHF.SetPixelData(f3data, 0);

        f3data.Dispose();
        f4data.Dispose();
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
