using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEditor;
using UnityEngine;

public class SyntheticParams : MonoBehaviour
{
    public Color m_Color = Color.white;

    public void OnDrawGizmos()
    {
        Gizmos.color = m_Color;
        Gizmos.DrawWireSphere(transform.position, Vector3.Dot(transform.lossyScale, new Vector3(0.33f,0.33f,0.33f))*0.55f);
    }

    static float InvSigmoid(float v)
    {
        v = Mathf.Min(v, 0.9999f);
        return Mathf.Log(v / (1.0f - v));
    }

    [MenuItem("Tools/Generate Synthetic Data")]
    public static void GenSynData()
    {
        var folderPath = EditorUtility.SaveFolderPanel("Save PLY from scene", "", "synthetic");
        if (string.IsNullOrWhiteSpace(folderPath))
            return;
        System.IO.Directory.CreateDirectory(folderPath);

        // cameras.json
        var cameras = FindObjectsOfType<Camera>();
        var cameraJsons = new List<string>();
        int camIndex = 0;
        foreach (var cam in cameras)
        {
            var tr = cam.transform;
            string json = $@"
{{""id"":{camIndex},
""img_name"":""dummy{camIndex}"",
""width"":4946,
""height"":3286,
""position"":[{tr.position.x}, {tr.position.y}, {tr.position.z}],
""rotation"":[
    [{tr.right.x}, {tr.right.y}, {tr.right.z}],
    [{tr.up.x}, {tr.up.y}, {tr.up.z}],
    [{tr.forward.x}, {tr.forward.y}, {tr.forward.z}]],
""fx"":4627.3,
""fy"":4649.5
}}";
            cameraJsons.Add(json);
            ++camIndex;
        }

        var allCameraJsons = "[\n" + string.Join(",", cameraJsons) + "\n]";
        File.WriteAllText($"{folderPath}/cameras.json", allCameraJsons);

        // cfg_args
        var cfg_args =
            "Namespace(eval=True, images='images_4', model_path='./eval/dummy', resolution=1, sh_degree=3, source_path='f:/dummy/dummy', white_background=False)";
        File.WriteAllText($"{folderPath}/cfg_args", cfg_args);

        // splats
        var splats = FindObjectsOfType<SyntheticParams>();

        System.IO.Directory.CreateDirectory($"{folderPath}/point_cloud/iteration_7000");
        FileStream fs = new FileStream($"{folderPath}/point_cloud/iteration_7000/point_cloud.ply", FileMode.Create,
            FileAccess.Write);
        fs.Write(Encoding.UTF8.GetBytes("ply\n"));
        fs.Write(Encoding.UTF8.GetBytes("format binary_little_endian 1.0\n"));
        fs.Write(Encoding.UTF8.GetBytes($"element vertex {splats.Length}\n"));
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

        NativeArray<GaussianSplatRenderer.InputSplat> splatData =
            new NativeArray<GaussianSplatRenderer.InputSplat>(splats.Length, Allocator.Temp);
        for (var si = 0; si < splats.Length; ++si)
        {
            SyntheticParams spl = splats[si];
            Transform tr = spl.transform;

            GaussianSplatRenderer.InputSplat dat = default;
            dat.pos = tr.position;
            dat.rot = tr.rotation;
            dat.rot = new Quaternion(dat.rot.w, dat.rot.x, dat.rot.y, dat.rot.w);
            dat.scale = tr.lossyScale;
            dat.scale = new Vector3(Mathf.Log(dat.scale.x), Mathf.Log(dat.scale.y), Mathf.Log(dat.scale.z));
            dat.opacity = InvSigmoid(spl.m_Color.a);
            //@TODO proper SH
            dat.dc0 = new Vector3(0.5f, 0.5f, 0.5f);

            splatData[si] = dat;
        }
        fs.Write(splatData.Reinterpret<byte>(UnsafeUtility.SizeOf<GaussianSplatRenderer.InputSplat>()));
        fs.Dispose();
    }
}
