using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Random = UnityEngine.Random;

public class GaussianSplatGenSynthetic : ScriptableWizard
{
    public enum SyntheticKind
    {
        RandomInsideSphere,
        RandomInsideBox,
        OrderedInsideBox
    }
    [FilePicker(nameKey:"SyntheticDataFolder")]
    public string m_Folder = "Assets/Models~/synthetic";

    public int m_SplatCount = 10000;
    public SyntheticKind m_Kind = SyntheticKind.RandomInsideSphere;
    public Vector3 m_PosRange = new Vector3(100,50,100);
    public Vector2 m_ScaleRange = new Vector2(0.01f, 3.0f);
    public Vector2 m_OpacityRange = new Vector2(0.1f, 1.0f);
    [Range(0, 1)] public float m_ScaleUniformness = 0.7f;
    public int m_RandomSeed = 1;

    [MenuItem("Tools/Gaussian Splats/Debug/Generate Synthetic PLY")]
    static void CreateWizard()
    {
        DisplayWizard<GaussianSplatGenSynthetic>("Generate Synthetic Gaussian Splat File", "Create");
    }

    void OnWizardCreate()
    {
        GenerateSyntheticData();
    }

    void OnWizardUpdate()
    {
        errorString = null;
        if (string.IsNullOrWhiteSpace(m_Folder))
            errorString = "Specify output folder";

        helpString =
            $"File size of {m_SplatCount:N0} splats: {EditorUtility.FormatBytes((long) m_SplatCount * UnsafeUtility.SizeOf<GaussianSplatAssetCreator.InputSplatData>())}";
    }

    static float InvSigmoid(float v)
    {
        v = Mathf.Min(v, 0.9999f);
        return Mathf.Log(v / (1.0f - v));
    }

    struct InputPoint
    {
        public Vector3 pos;
        public Vector3 nor;
        public Color32 col;
    }

    unsafe void GenerateSyntheticData()
    {
        helpString = null;
        Directory.CreateDirectory(m_Folder);

        // cameras.json
        var cameraJsons = new List<string>();
        {
            var pos = new Vector3(0, 0, -m_PosRange.z * 1.5f);
            var dirX = Vector3.right;
            var dirY = Vector3.up;
            var dirZ = Vector3.forward;

            pos.z *= -1;
            dirY *= -1;
            dirX.z *= -1;
            dirY.z *= -1;
            dirZ.z *= -1;
            string json = $@"{{""id"":0, ""img_name"":""dummy"", ""width"":1960, ""height"":1090, ""position"":[{pos.x}, {pos.y}, {pos.z}], ""rotation"":[[{dirX.x}, {dirY.x}, {dirZ.x}], [{dirX.y}, {dirY.y}, {dirZ.y}], [{dirX.z}, {dirY.z}, {dirZ.z}]], ""fx"":1160.3, ""fy"":1160.5}}";
            cameraJsons.Add(json);
        }

        var allCameraJsons = "[\n" + string.Join(",\n", cameraJsons) + "\n]";
        File.WriteAllText($"{m_Folder}/cameras.json", allCameraJsons);

        // cfg_args
        var cfg_args =
            "Namespace(eval=True, images='images_4', model_path='./eval/dummy', resolution=1, sh_degree=3, source_path='f:/dummy/dummy', white_background=False)";
        File.WriteAllText($"{m_Folder}/cfg_args", cfg_args);

        // splats
        Directory.CreateDirectory($"{m_Folder}/point_cloud/iteration_7000");
        FileStream fs = new FileStream($"{m_Folder}/point_cloud/iteration_7000/point_cloud.ply", FileMode.Create,
            FileAccess.Write);
        fs.Write(Encoding.UTF8.GetBytes("ply\n"));
        fs.Write(Encoding.UTF8.GetBytes("format binary_little_endian 1.0\n"));
        fs.Write(Encoding.UTF8.GetBytes($"element vertex {m_SplatCount}\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float x\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float y\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float z\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float nx\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float ny\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float nz\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_dc_0\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_dc_1\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_dc_2\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_0\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_1\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_2\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_3\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_4\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_5\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_6\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_7\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_8\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_9\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_10\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_11\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_12\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_13\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_14\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_15\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_16\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_17\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_18\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_19\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_20\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_21\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_22\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_23\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_24\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_25\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_26\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_27\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_28\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_29\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_30\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_31\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_32\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_33\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_34\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_35\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_36\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_37\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_38\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_39\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_40\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_41\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_42\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_43\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float f_rest_44\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float opacity\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float scale_0\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float scale_1\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float scale_2\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float rot_0\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float rot_1\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float rot_2\n"));
        fs.Write(Encoding.UTF8.GetBytes("property float rot_3\n"));
        fs.Write(Encoding.UTF8.GetBytes("end_header\n"));

        var fs2 = new FileStream($"{m_Folder}/input.ply", FileMode.Create, FileAccess.Write);
        fs2.Write(Encoding.UTF8.GetBytes("ply\n"));
        fs2.Write(Encoding.UTF8.GetBytes("format binary_little_endian 1.0\n"));
        fs2.Write(Encoding.UTF8.GetBytes($"element vertex {m_SplatCount}\n"));
        fs2.Write(Encoding.UTF8.GetBytes("property float x\n"));
        fs2.Write(Encoding.UTF8.GetBytes("property float y\n"));
        fs2.Write(Encoding.UTF8.GetBytes("property float z\n"));
        fs2.Write(Encoding.UTF8.GetBytes("property float nx\n"));
        fs2.Write(Encoding.UTF8.GetBytes("property float ny\n"));
        fs2.Write(Encoding.UTF8.GetBytes("property float nz\n"));
        fs2.Write(Encoding.UTF8.GetBytes("property uchar red\n"));
        fs2.Write(Encoding.UTF8.GetBytes("property uchar green\n"));
        fs2.Write(Encoding.UTF8.GetBytes("property uchar blue\n"));
        fs2.Write(Encoding.UTF8.GetBytes("property uchar alpha\n"));
        fs2.Write(Encoding.UTF8.GetBytes("end_header\n"));

        Random.InitState(m_RandomSeed == 0 ? Time.renderedFrameCount : m_RandomSeed);

        int countCubeRoot = Mathf.CeilToInt(Mathf.Pow(m_SplatCount, 1.0f / 3.0f));
        countCubeRoot = math.max(countCubeRoot, 2);
        for (var si = 0; si < m_SplatCount; ++si)
        {
            if (si % 100000 == 0)
                EditorUtility.DisplayProgressBar("Generating Splats", m_SplatCount.ToString("N0"), (float)si / (float)m_SplatCount);
            GaussianSplatAssetCreator.InputSplatData dat = default;
            switch (m_Kind)
            {
                case SyntheticKind.RandomInsideSphere:
                    dat.pos = Random.insideUnitSphere;
                    break;
                case SyntheticKind.RandomInsideBox:
                    dat.pos = new Vector3(Random.Range(-1.0f, 1.0f), Random.Range(-1.0f, 1.0f), Random.Range(-1.0f, 1.0f));
                    break;
                case SyntheticKind.OrderedInsideBox:
                    dat.pos.x = (si % countCubeRoot) * 2.0f / (countCubeRoot-1) - 1.0f;
                    dat.pos.y = ((si / countCubeRoot) % countCubeRoot) * 2.0f / (countCubeRoot-1) - 1.0f;
                    dat.pos.z = ((si / countCubeRoot / countCubeRoot) % countCubeRoot) * 2.0f / (countCubeRoot-1) - 1.0f;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            dat.pos.Scale(m_PosRange);
            dat.pos.z *= -1;
            dat.rot = Random.rotationUniform;
            // mirrored around Z axis
            dat.rot.x *= -1;
            dat.rot.y *= -1;
            // swizzle into their expected order
            dat.rot = new Quaternion(dat.rot.w, dat.rot.x, dat.rot.y, dat.rot.z);

            float scale1 = Random.Range(m_ScaleRange.x, m_ScaleRange.y);
            Vector3 scale2 = new Vector3(Random.Range(m_ScaleRange.x, m_ScaleRange.y), Random.Range(m_ScaleRange.x, m_ScaleRange.y), Random.Range(m_ScaleRange.x, m_ScaleRange.y));
            dat.scale = Vector3.Lerp(scale2, new Vector3(scale1, scale1, scale1), m_ScaleUniformness);
            dat.scale = new Vector3(Mathf.Log(dat.scale.x), Mathf.Log(dat.scale.y), Mathf.Log(dat.scale.z));
            dat.opacity = InvSigmoid(Random.Range(m_OpacityRange.x, m_OpacityRange.y));
            //@TODO proper SH
            dat.dc0 = new Vector3(2.0f, 1.0f, 0.5f);

            InputPoint pt = default;
            pt.pos = dat.pos;
            pt.col = Color.white;

            fs.Write(new ReadOnlySpan<byte>(UnsafeUtility.AddressOf(ref dat), UnsafeUtility.SizeOf<GaussianSplatAssetCreator.InputSplatData>()));
            fs2.Write(new ReadOnlySpan<byte>(UnsafeUtility.AddressOf(ref pt), UnsafeUtility.SizeOf<InputPoint>()));
        }
        EditorUtility.ClearProgressBar();
        fs.Dispose();
        fs2.Dispose();

        var info = $"Generated {m_SplatCount} splats into {m_Folder}";
        Debug.Log(info);
        helpString = info;
    }
}
